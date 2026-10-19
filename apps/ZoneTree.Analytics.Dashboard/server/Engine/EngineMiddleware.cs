using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using ZoneTree.Analytics;

enum EngineSource { Primary, Samples, Benchmark }

// Marks an endpoint that reads or writes an engine, and which one. Routing decides it, so any
// URL that reaches the endpoint, with or without a trailing slash, gets the engine.
sealed record EngineEndpoint(EngineSource Source, bool IsBeacon);

static class EngineEndpoints
{
  // Adds the engine to the endpoints of a group whose route pattern the filter accepts.
  public static TBuilder UseEngine<TBuilder>(this TBuilder builder, EngineSource source, Func<string, bool> pattern = null,
      bool beacon = false) where TBuilder : IEndpointConventionBuilder
  {
    builder.Add(endpoint =>
    {
      if (endpoint is RouteEndpointBuilder route && (pattern?.Invoke(route.RoutePattern.RawText ?? "") ?? true))
        endpoint.Metadata.Add(new EngineEndpoint(source, beacon));
    });
    return builder;
  }

  // Ad-hoc queries are for analysts; funnels and standard reports are for every role.
  public static TBuilder RequireQueryRoleForQueries<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
  {
    builder.Add(endpoint =>
    {
      if (endpoint is RouteEndpointBuilder route && IsQuery(route.RoutePattern.RawText))
        endpoint.Metadata.Add(new AuthorizeAttribute(DashboardAuthentication.QueryPolicy));
    });
    return builder;
  }

  public static bool IsQuery(string pattern) => pattern?.TrimEnd('/').EndsWith("/query", StringComparison.OrdinalIgnoreCase) == true;
}

// Gives reporting and tracker endpoints the engine they read or write. Primary endpoints lease
// the configured engine; in Development the sample and benchmark APIs lease their own datasets.
sealed class EngineMiddleware(RequestDelegate next, EngineHost engines, SettingsService settings, SiteResolution sites,
    IServiceProvider services)
{
  public const string ApiPath = "/_analytics/api";
  public const string SiteItem = "zt-dashboard-site";

  public async Task InvokeAsync(HttpContext context)
  {
    var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<EngineEndpoint>();
    if (endpoint == null)
    {
      await next(context);
      return;
    }
    if (endpoint.Source != EngineSource.Primary)
    {
      await SampleAsync(context, endpoint.Source == EngineSource.Benchmark);
      return;
    }
    if (endpoint.IsBeacon)
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
      if (!endpoint.IsBeacon) await context.Response.WriteAsJsonAsync(new { error = error.Message });
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
