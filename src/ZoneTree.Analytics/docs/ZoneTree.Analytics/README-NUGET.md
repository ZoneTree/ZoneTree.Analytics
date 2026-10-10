# ZoneTree.Analytics

**Embedded, privacy-conscious analytics for .NET.**

ZoneTree.Analytics runs inside your application. It records page views, sessions, custom events, telemetry and business metrics, and answers dashboard queries in milliseconds, with no analytics server, no external database and no dependency other than [ZoneTree](https://www.nuget.org/packages/ZoneTree).

```csharp
using ZoneTree.Analytics;

await using var analytics = AnalyticsEngine.Open("data/analytics");

analytics.Track(AnalyticsEvent.PageView(url, referrer, ipAddress, userAgent));
analytics.Track(new AnalyticsEvent("purchase")
{
    VisitorId = userId,
    Dimensions = { ["plan"] = "pro" },
    Values = { ["revenue"] = 49.0 },
});

var summary = await analytics.Web.GetSummaryAsync(new WebReportRequest
{
    Range = DateRange.LastDays(30),
    Compare = ComparisonPeriod.PreviousPeriod,
});

var bySource = await analytics.QueryAsync(new AnalyticsQuery
{
    Range = DateRange.LastDays(30),
    GroupBy = { StandardDimensions.Source },
    Metrics = { Metric.Visitors, Metric.BounceRate, Metric.ConversionRate("purchase") },
    OrderBy = { Order.By(Metric.Visitors) },
    Limit = 10,
});
```

* Web analytics: visitors, sessions, bounce rate, duration, entry and exit pages, sources, channels, UTM campaigns, geography, browsers, operating systems and devices.
* Generic events with custom dimensions and values.
* One query model: metrics from several events joined per bucket and group, time series, filters, comparisons, paging.
* Behavioral segments and funnels.
* Bot traffic recognized by name and category (search, AI, SEO, previews, monitors, scripts), excluded or reported next to page views.
* Exact-then-approximate unique visitors with mergeable HyperLogLog sketches.
* No cookies, no stored IP addresses or user agents, daily-rotating visitor hashes, configurable retention and visitor erasure.
* Extensible: event filters, enrichers, bot detectors, user agent parser and geolocator.
* Time-partitioned ZoneTree storage with write-ahead logging, crash recovery and bounded memory.

For ASP.NET Core, see [ZoneTree.Analytics.AspNetCore](https://www.nuget.org/packages/ZoneTree.Analytics.AspNetCore). Documentation: [github.com/ZoneTree/ZoneTree.Analytics](https://github.com/ZoneTree/ZoneTree.Analytics).
