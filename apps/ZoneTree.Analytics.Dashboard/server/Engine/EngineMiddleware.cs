using ZoneTree.Analytics;

// Gives reporting and tracker requests the engine they read or write. Primary requests lease the
// configured engine; in Development the sample and benchmark APIs lease their own datasets.
sealed class EngineMiddleware(RequestDelegate next, EngineHost engines, SettingsService settings, SiteResolution sites,
    IServiceProvider services)
{
  public const string ApiPath = "/_analytics/api";
  public const string EventPath = "/_analytics/event";
  public const string SiteItem = "zt-dashboard-site";

  public async Task InvokeAsync(HttpContext context)
  {
    var path = context.Request.Path;
    // Ad-hoc queries are for analysts; funnels and standard reports are for every role.
    if (path.StartsWithSegments("/_analytics") && path.Value.EndsWith("/query", StringComparison.OrdinalIgnoreCase)
        && DashboardAuthentication.User(context) is { CanQuery: false })
    {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      await context.Response.WriteAsJsonAsync(new { error = "The query explorer needs the analyst role. Ask the administrator for access." });
      return;
    }
    if (path.StartsWithSegments(SampleDataStore.ApiPath) || path.StartsWithSegments(BenchmarkReportStore.ApiPath))
    {
      await SampleAsync(context, path.StartsWithSegments(BenchmarkReportStore.ApiPath));
      return;
    }
    var isEvent = path.Equals(EventPath, StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(context.Request.Method);
    if (!isEvent && !path.StartsWithSegments(ApiPath))
    {
      await next(context);
      return;
    }
    // The dashboard configuration describes the host, not the engine.
    if (path.StartsWithSegments(ApiPath + "/config"))
    {
      await next(context);
      return;
    }
    if (isEvent)
    {
      var current = settings.Current;
      var site = current == null ? null : sites.Resolve(context, current);
      if (site == null)
      {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
      }
      context.Items[SiteItem] = site;
    }
    EngineLease lease;
    try
    {
      lease = await engines.AcquireAsync(context.RequestAborted);
    }
    catch (EngineUnavailableException error)
    {
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      context.Response.Headers.RetryAfter = "5";
      if (!isEvent) await context.Response.WriteAsJsonAsync(new { error = error.Message });
      return;
    }
    using (lease)
      await WithEngineAsync(context, lease.Engine);
  }

  // The endpoints resolve the engine from the request services. A wrapper hands out the leased
  // engine without owning it: a scoped registration would dispose it with the request scope.
  async Task WithEngineAsync(HttpContext context, AnalyticsEngine engine)
  {
    var original = context.RequestServices;
    context.RequestServices = new LeasedEngineServices(original, engine);
    try { await next(context); }
    finally { context.RequestServices = original; }
  }

  async Task SampleAsync(HttpContext context, bool benchmark)
  {
    if (services.GetService<SampleDataStore>() is not { } samples)
    {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
    }
    try
    {
      using var lease = benchmark
          ? services.GetRequiredService<SampleBenchmark>().Reports.Acquire()
          : samples.Acquire();
      await WithEngineAsync(context, lease.Engine);
    }
    catch (SampleDataBusyException)
    {
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      await context.Response.WriteAsJsonAsync(new { error = "Sample data is being prepared. Try refreshing shortly." });
    }
    catch (BenchmarkDataUnavailableException)
    {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      await context.Response.WriteAsJsonAsync(new { error = "Run a benchmark to create its report dataset." });
    }
  }
}

sealed class LeasedEngineServices(IServiceProvider original, AnalyticsEngine engine) : IServiceProvider
{
  public object GetService(Type serviceType) => serviceType == typeof(AnalyticsEngine) ? engine : original.GetService(serviceType);
}
