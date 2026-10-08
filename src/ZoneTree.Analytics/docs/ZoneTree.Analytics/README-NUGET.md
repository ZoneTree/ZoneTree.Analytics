# ZoneTree.Analytics

**Embedded, privacy-conscious analytics for .NET.**

ZoneTree.Analytics runs inside your application. It records page views, sessions, custom events, telemetry and business metrics, and answers dashboard queries in milliseconds, with no analytics server, no external database and no dependency other than [ZoneTree](https://www.nuget.org/packages/ZoneTree).

```csharp
using ZoneTree.Analytics;

using var analytics = AnalyticsEngine.Open("data/analytics");

analytics.Track(AnalyticsEvent.PageView(
    url: "https://example.com/pricing?utm_source=newsletter",
    referrer: "https://www.google.com/",
    ipAddress: "203.0.113.7",
    userAgent: userAgent));

analytics.Track(new AnalyticsEvent("purchase") { VisitorId = userId }
    .WithDimension("plan", "pro")
    .WithMetric("revenue", 49.0));

var from = DateTime.UtcNow.AddDays(-30);
var to = DateTime.UtcNow;
var summary = analytics.Web.GetSummary(from, to);   // visitors, page views, sessions, bounce rate, duration
var pages = analytics.Web.GetTopPages(from, to);
var sources = analytics.Web.GetSources(from, to);

var revenueByPlan = analytics.Query(new AnalyticsQuery()
    .ForEvent("purchase")
    .Between(from, to)
    .Every(TimeInterval.Week)
    .GroupBy("plan"));
```

* Web analytics: visitors, sessions, bounce rate, duration, entry and exit pages, sources, channels, UTM campaigns, geography, browsers, operating systems and devices.
* Generic events with custom dimensions and metrics.
* Time series, breakdowns, filters and top-N over pre-aggregated rollups, with automatic raw-event fallback.
* Exact-then-approximate unique visitors with mergeable HyperLogLog sketches.
* No cookies, no stored IP addresses or user agents, daily-rotating visitor hashes, configurable retention and visitor erasure.
* Time-partitioned ZoneTree storage with write-ahead logging, crash recovery and bounded memory.

For ASP.NET Core, see [ZoneTree.Analytics.AspNetCore](https://www.nuget.org/packages/ZoneTree.Analytics.AspNetCore). Documentation: [github.com/ZoneTree/ZoneTree.Analytics](https://github.com/ZoneTree/ZoneTree.Analytics).
