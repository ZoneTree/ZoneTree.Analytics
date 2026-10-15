using System.Net;
using ZoneTree.Analytics.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5090");
builder.Services.AddZoneTreeAnalytics(options =>
    options.DataDirectory = builder.Configuration["Analytics:DataDirectory"] ?? "data/analytics");

var app = builder.Build();
// The reporting API exposes analytics data. This example answers local requests only; a real
// application protects it with its own authorization, for example .RequireAuthorization().
app.Use(async (context, next) =>
{
  if (context.Request.Path.StartsWithSegments("/_analytics/api") &&
      !(context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address)))
  {
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    return;
  }
  await next(context);
});
app.UseZoneTreeAnalytics();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapZoneTreeAnalytics();
app.MapZoneTreeAnalyticsApi();
await app.RunAsync();
