# ZoneTree.Analytics.AspNetCore

ASP.NET Core integration for [ZoneTree.Analytics](https://github.com/ZoneTree/ZoneTree.Analytics), the embedded, privacy-conscious analytics engine for .NET.

```csharp
builder.Services.AddZoneTreeAnalytics(options =>
{
    options.DataDirectory = "data/analytics";
});

var app = builder.Build();

// Either track page views on the server...
app.UseZoneTreeAnalytics();

// ...or serve the cookie-less JavaScript tracker and its beacon endpoint.
app.MapZoneTreeAnalytics();
```

```html
<script defer src="/_analytics/script.js"></script>
<script>
  window.zta = window.zta || function () { (zta.q = zta.q || []).push(arguments) };
  zta('signup', { plan: 'pro' });
</script>
```

Query the injected `AnalyticsEngine` anywhere:

```csharp
app.MapGet("/stats", (AnalyticsEngine analytics) =>
    analytics.Web.GetSummary(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow))
   .RequireAuthorization();
```
