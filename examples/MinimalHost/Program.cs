using ZoneTree.Analytics.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5090");
builder.Services.AddZoneTreeAnalytics(options =>
    options.DataDirectory = builder.Configuration["Analytics:DataDirectory"] ?? "data/analytics");

var app = builder.Build();
app.UseZoneTreeAnalytics();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapZoneTreeAnalytics();
app.MapZoneTreeAnalyticsApi();
await app.RunAsync();
