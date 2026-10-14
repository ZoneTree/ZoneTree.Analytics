using ZoneTree.Analytics.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddZoneTreeAnalytics(
    options => options.DataDirectory = "data/analytics");

var app = builder.Build();
app.UseZoneTreeAnalytics();
app.MapGet("/", () => Results.Content(
    "<!doctype html><html><body><h1>Hello analytics</h1></body></html>",
    "text/html"));
app.MapZoneTreeAnalyticsApi();
await app.RunAsync();

