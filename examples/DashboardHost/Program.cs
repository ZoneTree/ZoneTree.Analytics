using Microsoft.Extensions.FileProviders;
using ZoneTree.Analytics;
using ZoneTree.Analytics.AspNetCore;
using ZoneTree.Analytics.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5088");
var contentRoot = builder.Environment.ContentRootPath;
var watchDashboard = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue("Dashboard:Watch", true);
var dashboardPath = Path.GetFullPath(builder.Configuration["Dashboard:Path"]
    ?? Path.Combine(contentRoot, "../../dashboard/dist/publish"));
if (!watchDashboard && !File.Exists(Path.Combine(dashboardPath, "index.html")))
  throw new InvalidOperationException("Build the dashboard first: cd dashboard; yarn install; yarn publish. " +
      "Or supply --Dashboard:Path=<absolute path to the generated site>.");
var timeZone = TimeZoneInfo.FindSystemTimeZoneById(builder.Configuration["Analytics:TimeZone"] ?? "UTC");
var sites = builder.Configuration.GetSection("Dashboard:Sites").Get<DashboardSite[]>()
    ?? [new("", "Default site")];
var sampleDataEnabled = builder.Environment.IsDevelopment();
if (sampleDataEnabled)
{
  builder.Services.AddSingleton<SampleDataStore>();
  builder.Services.AddSingleton(services => new SampleBenchmark(contentRoot,
      TimeZoneInfo.FindSystemTimeZoneById(builder.Configuration["Analytics:TimeZone"] ?? "UTC"), services.GetRequiredService<ILogger<SampleBenchmark>>()));
  builder.Services.AddSingleton<SampleDataJobs>();
  builder.Services.AddHostedService(services => services.GetRequiredService<SampleDataJobs>());
  sites = [.. sites.Where(site => site.Id != SampleDataStore.Site && site.Id != BenchmarkReportStore.Site),
      new(SampleDataStore.Site, "Performance samples"), new(BenchmarkReportStore.Site, "Latest benchmark")];
}
var refreshSeconds = builder.Configuration.GetValue("Dashboard:RefreshSeconds", 30);
const string apiPath = "/_analytics/api";
if (watchDashboard)
{
  builder.Services.AddSingleton<DashboardDevelopmentServer>();
  builder.Services.AddHostedService(services => services.GetRequiredService<DashboardDevelopmentServer>());
}

builder.Services.AddZoneTreeAnalytics(options =>
{
  options.DataDirectory = builder.Configuration["Analytics:DataDirectory"] ?? "data/analytics";
  options.TimeZone = timeZone;
  options.Web.Bots = BotTraffic.TrackSeparately;
  options.Web.GeoLocator = new ExampleGeoLocator();
  options.Rollups.ValueAggregates["load_time"] = ValueAggregates.Percentiles;
  SampleAnalyticsOptions.ConfigureIndexes(options);
});

var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Analytics:SeedDemo"))
{
  var engine = app.Services.GetRequiredService<AnalyticsEngine>();
  if (engine.GetEventNames(DateRange.LastDays(60)).Count == 0)
    await SampleTraffic.SeedAsync(engine);
}

// In an existing production host, put your authorization policy on both
// reporting routes and dashboard access.
var reportingApi = app.MapZoneTreeAnalyticsApi(apiPath);
app.MapZoneTreeAnalytics();
if (sampleDataEnabled)
{
  app.Use(async (context, next) =>
  {
    var isBenchmark = context.Request.Path.StartsWithSegments(BenchmarkReportStore.ApiPath);
    if (!isBenchmark && !context.Request.Path.StartsWithSegments(SampleDataStore.ApiPath)) { await next(context); return; }
    var original = context.RequestServices;
    try
    {
      using var lease = isBenchmark
          ? app.Services.GetRequiredService<SampleBenchmark>().Reports.Acquire()
          : app.Services.GetRequiredService<SampleDataStore>().Acquire();
      context.RequestServices = new SampleRequestServices(original, lease.Engine);
      try { await next(context); }
      finally { context.RequestServices = original; }
    }
    catch (SampleDataBusyException)
    {
      context.Response.StatusCode = 503;
      await context.Response.WriteAsJsonAsync(new { error = "Sample data is being prepared. Try refreshing shortly." });
    }
    catch (BenchmarkDataUnavailableException)
    {
      context.Response.StatusCode = 404;
      await context.Response.WriteAsJsonAsync(new { error = "Run a benchmark to create its report dataset." });
    }
  });
  app.MapZoneTreeAnalyticsApi(SampleDataStore.ApiPath);
  app.MapZoneTreeAnalyticsApi(BenchmarkReportStore.ApiPath);
  var management = app.MapGroup("/_analytics/sample-data");
  management.AddEndpointFilter(async (context, next) =>
  {
    var request = context.HttpContext.Request;
    if (!HttpMethods.IsGet(request.Method) && request.Headers.Origin.Count > 0
        && request.Headers.Origin.ToString() != $"{request.Scheme}://{request.Host}")
      return Results.Json(new { error = "Use sample controls from this host." }, statusCode: 403);
    context.HttpContext.Response.Headers.CacheControl = "no-store";
    return await next(context);
  });
  management.MapGet("/status", (SampleDataJobs jobs) => Results.Json(jobs.Status));
  management.MapPost("/generate", (SampleGenerationRequest request, SampleDataJobs jobs) =>
  {
    try { return jobs.Generate(request) ? Results.Json(jobs.Status, statusCode: 202) : Results.Conflict(new { error = "A sample operation is already running." }); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
  });
  management.MapPost("/cancel", (System.Text.Json.JsonElement body, SampleDataJobs jobs) => { jobs.Cancel(); return Results.Json(jobs.Status); });
  management.MapPost("/benchmark", (SampleBenchmarkRequest request, SampleDataJobs jobs) =>
  {
    try { return jobs.Benchmark(request) ? Results.Json(jobs.Status, statusCode: 202) : Results.Conflict(new { error = "A sample operation is already running." }); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
  });
  management.MapPost("/clear", (System.Text.Json.JsonElement body, SampleDataJobs jobs) =>
      jobs.Clear() ? Results.Json(jobs.Status, statusCode: 202) : Results.Conflict(new { error = "Cancel the running operation before deleting sample data." }));
  management.MapPost("/clear-benchmark", (System.Text.Json.JsonElement body, SampleDataJobs jobs) =>
      jobs.ClearBenchmark() ? Results.Json(jobs.Status, statusCode: 202) : Results.Conflict(new { error = "Cancel the running operation before deleting benchmark data." }));
}
reportingApi.MapGet("/config", (HttpContext context) =>
{
  context.Response.Headers.CacheControl = "no-store";
  return Results.Json(new
  {
    mode = "live",
    apiBaseUrl = apiPath,
    timeZone = timeZone.Id,
    sites,
    refreshSeconds,
    sampleData = sampleDataEnabled ? new { siteId = SampleDataStore.Site, apiBaseUrl = SampleDataStore.ApiPath,
      managementUrl = "/_analytics/sample-data", benchmark = new { siteId = BenchmarkReportStore.Site, apiBaseUrl = BenchmarkReportStore.ApiPath } } : null,
  });
});
using var files = watchDashboard ? null : new PhysicalFileProvider(dashboardPath);
if (watchDashboard)
  app.MapFallback("/{**path}", app.Services.GetRequiredService<DashboardDevelopmentServer>().ForwardAsync);
else
{
  app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files! });
  app.UseStaticFiles(new StaticFileOptions { FileProvider = files! });
}
// Complete migrations before browsers start their reporting request timeouts.
if (sampleDataEnabled)
{
  var samples = app.Services.GetRequiredService<SampleDataStore>();
  if (samples.HasRetainedData)
    using (samples.Acquire()) { }
  var benchmark = app.Services.GetRequiredService<SampleBenchmark>();
  if (benchmark.Reports.Result != null)
    using (benchmark.Reports.Acquire()) { }
}
await app.RunAsync();

sealed record DashboardSite(string Id, string Name);

sealed class ExampleGeoLocator : IGeoLocator
{
  public GeoLocation Locate(string ipAddress) =>
      new(ipAddress.EndsWith(".1", StringComparison.Ordinal) ? "US" : "DE");
}
