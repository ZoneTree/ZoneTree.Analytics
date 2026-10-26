using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using ZoneTree.Analytics;
using ZoneTree.Analytics.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5088");
var contentRoot = builder.Environment.ContentRootPath;
var watchDashboard = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue("Dashboard:Watch", true);
var dashboardPath = Path.GetFullPath(builder.Configuration["Dashboard:Path"]
    ?? Path.Combine(contentRoot, "../ui/dist/publish"));
if (!watchDashboard && !File.Exists(Path.Combine(dashboardPath, "index.html")))
  throw new InvalidOperationException("Build the dashboard first: cd apps/ZoneTree.Analytics.Dashboard/ui; yarn install; yarn publish. " +
      "Or supply --Dashboard:Path=<absolute path to the generated site>.");

// Users, sessions, settings and the audit log. Everything else is configured in the dashboard.
var systemDirectory = builder.Configuration["Dashboard:SystemDirectory"] ?? SystemStore.DefaultDirectory;
var systemStore = new SystemStore(Path.IsPathRooted(systemDirectory) ? systemDirectory : Path.Combine(contentRoot, systemDirectory));
builder.Services.AddSingleton(systemStore);
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<EngineHost>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<SignInThrottle>();
builder.Services.AddSingleton<SetupKey>();
builder.Services.AddSingleton<SetupStatus>();
builder.Services.AddSingleton<SiteResolution>();
builder.Services.AddHostedService<SystemCleanup>();
builder.Services.AddDashboardAuthentication();
// Behind a reverse proxy that terminates HTTPS, the origin check, secure cookies and visitor
// addresses need the original scheme, host and client address. Loopback proxies are trusted by
// default; list others with Dashboard:KnownProxies (comma-separated addresses).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
  options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
  foreach (var proxy in (builder.Configuration["Dashboard:KnownProxies"] ?? "").Split(',',
      StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    options.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.AddZoneTreeAnalyticsWeb(web =>
{
  web.MaxConcurrentReportingRequests = SettingsService.StoredConcurrentRequests(systemStore);
  web.SiteResolver = context => context.Items[EngineMiddleware.SiteItem] as string;
  web.AuthorizeSite = (context, site) => DashboardAuthentication.User(context)?.CanAccess(site) == true;
});

var benchmarkEnabled = builder.Environment.IsDevelopment();
if (benchmarkEnabled)
{
  // A benchmark run takes the reporting time zone current when it starts.
  builder.Services.AddSingleton(services => new IngestionBenchmark(contentRoot,
      () => services.GetRequiredService<SettingsService>().ReportingTimeZone, services.GetRequiredService<ILogger<IngestionBenchmark>>(),
      () => services.GetRequiredService<EngineHost>().IpLocation));
  builder.Services.AddSingleton<BenchmarkJobs>();
  builder.Services.AddHostedService(services => services.GetRequiredService<BenchmarkJobs>());
}
if (watchDashboard)
{
  builder.Services.AddSingleton<DashboardDevelopmentServer>();
  builder.Services.AddHostedService(services => services.GetRequiredService<DashboardDevelopmentServer>());
}

var app = builder.Build();
var settings = app.Services.GetRequiredService<SettingsService>();
var engines = app.Services.GetRequiredService<EngineHost>();
var setup = app.Services.GetRequiredService<SetupStatus>();
// Open the engine now: data it cannot load is reported at startup and in the admin panel.
if (settings.IsConfigured)
{
  await engines.OpenAsync(settings.Current);
  if (engines.State == EngineState.Running && builder.Configuration.GetValue<bool>("Analytics:SeedDemo"))
  {
    using var lease = await engines.AcquireAsync(CancellationToken.None);
    if (lease.Engine.GetEventNames(DateRange.LastDays(60)).Count == 0)
      await SampleTraffic.SeedAsync(lease.Engine);
  }
}

app.UseForwardedHeaders();
app.UseMiddleware<FrameProtectionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<SameOriginMiddleware>();
app.UseMiddleware<PageGateMiddleware>();
using var files = watchDashboard ? null : new PhysicalFileProvider(dashboardPath);
if (!watchDashboard)
{
  app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
  app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}
app.UseAuthorization();
app.UseMiddleware<EngineMiddleware>();

var reportingApi = app.MapZoneTreeAnalyticsApi(EngineMiddleware.ApiPath)
    .RequireAuthorization()
    .RequireQueryRoleForQueries()
    .UseEngine(EngineSource.Primary, pattern => !pattern.TrimEnd('/').EndsWith("/config", StringComparison.OrdinalIgnoreCase));
app.MapZoneTreeAnalytics()
    .UseEngine(EngineSource.Primary, pattern => pattern.TrimEnd('/').EndsWith("/event", StringComparison.OrdinalIgnoreCase), beacon: true);
app.MapDashboardAuth();
app.MapDashboardSetup();
app.MapDashboardAdmin();
reportingApi.MapGet("/config", (HttpContext context) =>
{
  context.Response.Headers.CacheControl = "no-store";
  var user = DashboardAuthentication.User(context);
  var current = settings.Current;
  var sites = current?.Sites.Items.Where(site => user.CanAccess(site.Id)).Select(site => new SiteView(site.Id, site.Name)).ToList() ?? [];
  if (user.IsAdministrator && current?.Sites.TrackDashboardUsage == true)
    sites.Add(new(SettingsMapper.DashboardUsageSite, "Dashboard usage"));
  var benchmark = benchmarkEnabled && user.IsAdministrator;
  // The benchmark site is listed while a completed run is retained.
  if (benchmark && context.RequestServices.GetRequiredService<IngestionBenchmark>().Reports.Result != null)
    sites.Add(new(BenchmarkReportStore.Site, "Latest benchmark"));
  return Results.Json(new
  {
    mode = "live",
    apiBaseUrl = EngineMiddleware.ApiPath,
    timeZone = current?.General.TimeZone ?? "UTC",
    sites,
    refreshSeconds = current?.General.RefreshSeconds ?? 30,
    benchmark = benchmark ? new
    {
      siteId = BenchmarkReportStore.Site,
      apiBaseUrl = BenchmarkReportStore.ApiPath,
      managementUrl = BenchmarkReportStore.ManagementPath
    } : null,
    user = new { id = user.Id, userName = user.UserName, displayName = user.DisplayName, role = user.Role.ToString(), canQuery = user.CanQuery },
  });
});
if (benchmarkEnabled)
{
  app.MapZoneTreeAnalyticsApi(BenchmarkReportStore.ApiPath)
      .RequireAuthorization(DashboardAuthentication.AdministratorPolicy).UseEngine(EngineSource.Benchmark);
  var management = app.MapGroup(BenchmarkReportStore.ManagementPath).RequireAuthorization(DashboardAuthentication.AdministratorPolicy);
  management.AddEndpointFilter(async (context, next) =>
  {
    context.HttpContext.Response.Headers.CacheControl = "no-store";
    return await next(context);
  });
  management.MapGet("/status", (BenchmarkJobs jobs) => Results.Json(jobs.Status));
  management.MapPost("/cancel", (System.Text.Json.JsonElement body, BenchmarkJobs jobs) => { jobs.Cancel(); return Results.Json(jobs.Status); });
  management.MapPost("/run", (BenchmarkRequest request, BenchmarkJobs jobs) =>
  {
    try { return jobs.Run(request) ? Results.Json(jobs.Status, statusCode: 202) : Results.Conflict(new { error = "A benchmark operation is already running." }); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
  });
  management.MapPost("/clear", (System.Text.Json.JsonElement body, BenchmarkJobs jobs) =>
      jobs.Clear() ? Results.Json(jobs.Status, statusCode: 202) : Results.Conflict(new { error = "Cancel the running operation before deleting benchmark data." }));
}
if (watchDashboard)
  app.MapFallback("/{**path}", app.Services.GetRequiredService<DashboardDevelopmentServer>().ForwardAsync);
// Complete migrations before browsers start their reporting request timeouts. A Development
// dataset that cannot open is reported on its pages; it does not stop the dashboard.
if (benchmarkEnabled)
{
  try
  {
    var benchmark = app.Services.GetRequiredService<IngestionBenchmark>();
    if (benchmark.Reports.Result != null)
      using (benchmark.Reports.Acquire()) { }
  }
  catch (Exception error) when (error is AnalyticsConfigurationException or IOException)
  {
    app.Logger.LogError(error, "The retained benchmark dataset could not open. Delete it on the Benchmark page and run a new benchmark.");
  }
}
app.Lifetime.ApplicationStarted.Register(() =>
{
  var displayAddresses = app.Urls.Select(address =>
      Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host is "0.0.0.0" or "[::]"
          ? new UriBuilder(uri) { Host = "localhost" }.Uri.AbsoluteUri
          : address).ToList();
  foreach (var address in displayAddresses)
    app.Logger.LogInformation("Open dashboard: {Url}", address);
  if (!setup.Complete && displayAddresses.Count > 0)
    _ = SetupBanner.ShowAsync(displayAddresses[0], app.Services.GetRequiredService<SetupKey>(), setup,
        watchDashboard ? app.Services.GetRequiredService<DashboardDevelopmentServer>() : null, app.Lifetime.ApplicationStopping);
});
app.Lifetime.ApplicationStopped.Register(() => systemStore.Dispose());
await app.RunAsync();
