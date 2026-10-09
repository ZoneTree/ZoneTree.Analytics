# ZoneTree.Analytics.AspNetCore

ASP.NET Core integration for [ZoneTree.Analytics](https://github.com/ZoneTree/ZoneTree.Analytics), the embedded, privacy-conscious analytics engine for .NET.

```csharp
builder.Services.AddZoneTreeAnalytics(
    options => options.DataDirectory = "data/analytics",
    web => web.AuthorizeSite = (context, site) => context.User.IsInRole("analytics"));

var app = builder.Build();
app.UseForwardedHeaders();

app.UseZoneTreeAnalytics();             // track HTML page views on the server, or...
app.MapZoneTreeAnalytics();             // ...serve /_analytics/script.js and its beacon endpoint
app.MapZoneTreeAnalyticsApi()           // JSON reporting API under /_analytics/api
   .RequireAuthorization();
```

```html
<script defer src="/_analytics/script.js"></script>
<script>
  window.zta = window.zta || function () { (zta.q = zta.q || []).push(arguments) };
  zta('signup', { plan: 'pro' });
</script>
```

* `AddZoneTreeAnalytics` registers a singleton `AnalyticsEngine`, resolves the data directory against the content root and routes engine logs to `Microsoft.Extensions.Logging`.
* The tracker is cookie-less, follows single-page-app navigation and uses `sendBeacon`.
* The reporting API serves summaries, time series, breakdowns, pages, goals, bots, real-time visitors, arbitrary queries and funnels as JSON.
