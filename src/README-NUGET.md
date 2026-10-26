![ZoneTree.Analytics: Millions of events. One clear picture. Embedded analytics for .NET.](https://raw.githubusercontent.com/ZoneTree/ZoneTree.Analytics/main/docs-site/content/assets/readme-banner.svg)

# ZoneTree.Analytics

**Understand your traffic. Measure your product. Own your data.**

Web analytics, custom events, and telemetry inside your .NET application.

[![ZoneTree.Analytics on NuGet](https://img.shields.io/nuget/v/ZoneTree.Analytics?style=flat-square&label=NuGet&color=fb7700)](https://www.nuget.org/packages/ZoneTree.Analytics/)
[![Targets .NET 8, 9, and 10](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512bd4?style=flat-square)](https://github.com/ZoneTree/ZoneTree.Analytics)
[![MIT license](https://img.shields.io/badge/license-MIT-488968?style=flat-square)](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/LICENSE)

ZoneTree.Analytics is an **embedded analytics engine** built on [ZoneTree](https://www.nuget.org/packages/ZoneTree/). Record events, keep the dataset on your infrastructure, and query it directly from C#. The engine runs in your process and requires no external database or separate analytics service.

## Packages

| Package | What it adds |
| --- | --- |
| [ZoneTree.Analytics](https://www.nuget.org/packages/ZoneTree.Analytics/) | The engine: background ingestion, persistent storage, retention, web reports, and the typed C# query API. Its only direct package dependency is ZoneTree. |
| [ZoneTree.Analytics.AspNetCore](https://www.nuget.org/packages/ZoneTree.Analytics.AspNetCore/) | ASP.NET Core integration: dependency injection, server-side page view tracking, a cookie-free browser tracker, and a reporting API. |

Both packages target **.NET 8, 9, and 10**.

## What you can build

| Use case | What you can measure |
| --- | --- |
| **Website analytics** | Visitors, page views, sessions, bounce rate, popular pages, sources, campaigns, browsers, and devices |
| **Product analytics** | Signups, purchases, revenue, behavioral segments, and conversion funnels |
| **Service telemetry** | Event counts, durations, percentiles, histograms, error rates, and business metrics |
| **Multi-site reporting** | Site-scoped events and reports for multiple websites, applications, or tenants in one engine |

- **One query model.** Combine metrics, filters, dimensions, formulas, time buckets, and period comparisons through a typed C# API.
- **Pre-aggregation for recurring reports.** Matching rollups and optional indexes answer recurring reports; queries they cannot answer fall back to retained event history. Query plans expose coverage and warnings.
- **Privacy-conscious defaults.** Cookie-free collection, daily anonymous identity rotation, and no stored raw IP addresses or user-agent strings.
- **Application-owned storage.** Background ingestion, configurable retention, and persistent ZoneTree stores.
- **Room to customize.** Plug in event filters, enrichers, bot detectors, a user-agent parser, or a geolocation provider. Bot traffic can be excluded or reported separately.

## Quick start

In a .NET console project, install the engine:

```sh
dotnet add package ZoneTree.Analytics
```

Replace `Program.cs` with this complete example:

```csharp
using ZoneTree.Analytics;
using ZoneTree.Analytics.Web;

await using var analytics = AnalyticsEngine.Open("data/analytics");

// Track a page view and a business event for the same identified visitor.
var pageView = AnalyticsEvent.PageView("https://example.com/pricing");
pageView.VisitorId = "customer-42";
await analytics.TrackAsync(pageView);

await analytics.TrackAsync(new AnalyticsEvent("purchase")
{
    VisitorId = "customer-42",
    Dimensions = { ["plan"] = "pro" },
    Values = { ["revenue"] = 49.0 },
});

// Make the events visible before reading them in this short-lived example.
await analytics.FlushAsync();

var traffic = await analytics.Web.GetSummaryAsync(new WebReportRequest
{
    Range = DateRange.LastDays(30),
});

var sales = await analytics.QueryAsync(new AnalyticsQuery
{
    Event = "purchase",
    Range = DateRange.LastDays(30),
    Metrics = { Metric.Events, Metric.Sum("revenue") },
});

Console.WriteLine($"Page views: {traffic.Summary.PageViews}");
Console.WriteLine($"Revenue: {sales.Totals[Metric.Sum("revenue")]:F2}");
```

Run it with `dotnet run`. On an empty dataset, it records one page view and revenue of 49.00. The data remains in `data/analytics` between runs.

Keep **one engine per data directory** open for the application's lifetime. Events are processed in the background, with an aggregate flush interval of two seconds by default. Normal reporting does not need an explicit flush; this example uses one so its immediate read includes the events it just tracked. Await disposal at shutdown to drain the queue and flush remaining work.

## Add it to ASP.NET Core

Install the integration package in an ASP.NET Core project:

```sh
dotnet add package ZoneTree.Analytics.AspNetCore
```

This minimal `Program.cs` registers the engine and tracks successful HTML page responses:

```csharp
using ZoneTree.Analytics.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddZoneTreeAnalytics(
    options => options.DataDirectory = "data/analytics");

var app = builder.Build();
app.UseZoneTreeAnalytics();

app.MapGet("/", () => Results.Content(
    "<!doctype html><html><body><h1>Hello analytics</h1></body></html>",
    "text/html"));

await app.RunAsync();
```

Dependency injection owns the shared engine and disposes it at host shutdown. Inject `IAnalyticsTracker` to record custom actions, or `AnalyticsEngine` to query reports.

- **Browser collection:** map `app.MapZoneTreeAnalytics()` and load `/_analytics/script.js` on your pages. Choose either server middleware or browser tracking for each page view to avoid double counting.
- **Reporting API:** map `app.MapZoneTreeAnalyticsApi()`, and protect it with `app.MapZoneTreeAnalyticsApi().RequireAuthorization()` in a host with authentication and authorization configured. Multi-site applications should also configure `AuthorizeSite` for site-level access.

The [ASP.NET Core guide](https://analytics.zonetree.dev/docs/getting-started/aspnet-core/) covers collection choices, proxy configuration, and reporting access.

## Ask more of your events

The general query API supports totals, grouped reports, time series, and formulas. After tracking purchases with a `plan` dimension and a `revenue` value:

```csharp
var revenue = Metric.Sum("revenue");
var averageOrder = revenue / Metric.Events;

var report = await analytics.QueryAsync(new AnalyticsQuery
{
    Event = "purchase",
    Range = DateRange.ThisMonth,
    Interval = TimeInterval.Day,
    GroupBy = { "plan" },
    Metrics = { Metric.Events, revenue, averageOrder },
    Compare = ComparisonPeriod.PreviousPeriod,
});

foreach (var row in report.Rows)
    Console.WriteLine($"{row.Bucket:yyyy-MM-dd} | {row["plan"]}: {row[revenue]:F2} revenue");

Console.WriteLine($"Average order: {report.Totals[averageOrder]:F2}");
```

Totals evaluate formulas over the range's totals. Here, the overall average order value is total revenue divided by the number of purchases.

| Question | Tools |
| --- | --- |
| Where do visitors come from? | Web breakdowns by source, channel, campaign, and referrer |
| Which plans generate revenue? | Custom dimensions, numeric aggregates, and metric formulas |
| What did purchasers view? | Visitor or session segments based on recorded behavior |
| Where do visitors drop off? | Ordered funnels with a configurable conversion window |
| How slow are background jobs? | Percentiles, histograms, moving averages, and time buckets |

Explore [querying](https://analytics.zonetree.dev/docs/guides/querying/), [segments and funnels](https://analytics.zonetree.dev/docs/guides/segments-and-funnels/), and the [query reference](https://analytics.zonetree.dev/docs/reference/query-model/) for supported combinations and metric semantics.

## A dashboard you can host

Explore traffic, acquisition, audience, events, funnels, and bot activity, run custom queries, and export reports. The dashboard is a .NET server with a static UI, built on these packages. It ships as source in the [GitHub repository](https://github.com/ZoneTree/ZoneTree.Analytics), not as a NuGet package.

[![ZoneTree Analytics dashboard showing visitor trends, page views, sessions, and acquisition reports](https://raw.githubusercontent.com/ZoneTree/ZoneTree.Analytics/main/docs-site/content/docs/getting-started/dashboard/dashboard-overview.png)](https://analytics.zonetree.dev/docs/getting-started/dashboard/)

The [dashboard guide](https://analytics.zonetree.dev/docs/getting-started/dashboard/) covers running it with sample data, release builds, and deployment.

## Performance

A [documented benchmark](https://analytics.zonetree.dev/docs/performance/query-benchmarks/) measures annual reports with previous-period comparisons over 5 million generated events spanning 365 days, including configuration, query plans, individual timings, and methodology.

Matching rollups answer recurring reports from aggregates. Segments, funnels, and unsupported aggregate combinations can require event scans. The [benchmarking guide](https://analytics.zonetree.dev/docs/performance/benchmarking/) explains how to measure your own workload.

## Privacy and data ownership

The default identity strategy recognizes anonymous visitors within a UTC day using a rotating hash. Explicit visitor IDs are keyed hashes and remain stable across days. Raw IP addresses and user-agent strings are used in memory for identity and enrichment, then discarded. Page query strings are excluded by default.

Custom dimensions retain the values your application supplies, so choose what to collect. Configure dimension allow and deny lists, geography precision, identity strategy, and retention to fit your use case. Geography enrichment requires a geolocation provider: `IpCountryDatabase` resolves countries from a free, downloadable database.

Visitor erasure removes an explicit visitor's retained raw events, dimension-index copies, and active session state. **Existing aggregate rollups are not recomputed.** Read [privacy and identity](https://analytics.zonetree.dev/docs/concepts/privacy/) and [retention and erasure](https://analytics.zonetree.dev/docs/operations/retention-and-erasure/) for the full behavior.

## Operating the engine

| Concern | What to know |
| --- | --- |
| **Storage ownership** | One engine exclusively owns a data directory, including across processes. Keep it on persistent storage. Replicas with separate directories have separate datasets. |
| **Ingestion pressure** | The queue is bounded. `Track` drops events on a full queue by default; `TrackAsync` waits asynchronously for space. Both return whether the event was accepted. Monitor dropped-event statistics and configure request tracking's queue behavior as needed. |
| **Freshness and durability** | Aggregates are buffered until a successful flush. A crash can lose unflushed aggregates; persistent records follow ZoneTree's write-ahead log guarantees. Await clean shutdown. |
| **History** | Raw events default to 90 days; hourly rollups to 400 days; daily rollups are kept indefinitely. Hour and Day are the default enabled rollup tiers. |
| **Coverage and counts** | Inspect query plans and warnings when history or aggregates cannot cover a request. At default precision, unique counts are exact up to 4,096 identities per row, then have about 0.8% standard error. |

Use `analytics.GetStatistics()` and host logging to monitor the engine. See [deployment and lifecycle](https://analytics.zonetree.dev/docs/operations/deployment/) for scaling and backups, and [time and accuracy](https://analytics.zonetree.dev/docs/concepts/time-and-accuracy/) for identity, count, and reporting semantics.

## Documentation

| Start here | Go deeper |
| --- | --- |
| [Getting started](https://analytics.zonetree.dev/docs/getting-started/) | [Architecture](https://analytics.zonetree.dev/docs/concepts/architecture/) |
| [ASP.NET Core integration](https://analytics.zonetree.dev/docs/getting-started/aspnet-core/) | [Configuration reference](https://analytics.zonetree.dev/docs/reference/configuration/) |
| [Web reports](https://analytics.zonetree.dev/docs/guides/web-reports/) | [Rollups and query planning](https://analytics.zonetree.dev/docs/performance/rollups/) |
| [Product analytics](https://analytics.zonetree.dev/docs/guides/product-analytics/) | [Secondary indexes](https://analytics.zonetree.dev/docs/performance/secondary-indexes/) |
| [Service telemetry](https://analytics.zonetree.dev/docs/guides/telemetry/) | [HTTP API reference](https://analytics.zonetree.dev/docs/reference/http-api/) |
| [Sites and tenants](https://analytics.zonetree.dev/docs/guides/sites-and-tenants/) | [Extensibility](https://analytics.zonetree.dev/docs/guides/extensibility/) |

## Feedback

Bug reports and focused pull requests are welcome on [GitHub](https://github.com/ZoneTree/ZoneTree.Analytics). For a reproducible report, include the package version, runtime, relevant options, and a small example in a [GitHub issue](https://github.com/ZoneTree/ZoneTree.Analytics/issues).

## License

Released under the [MIT license](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/LICENSE). Built on [ZoneTree](https://github.com/ZoneTree/ZoneTree).
