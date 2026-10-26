<p align="center">
  <img src="https://raw.githubusercontent.com/ZoneTree/ZoneTree.Analytics/main/docs-site/content/assets/readme-banner.svg" alt="ZoneTree.Analytics — Millions of events. One clear picture. Embedded analytics for .NET." width="1280" />
</p>

<h1 align="center">ZoneTree.Analytics</h1>

<p align="center">
  <strong>Understand your traffic. Measure your product. Own your data.</strong><br />
  Web analytics, custom events, and telemetry inside your .NET application.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/ZoneTree.Analytics/"><img src="https://img.shields.io/nuget/v/ZoneTree.Analytics?style=flat-square&amp;label=NuGet&amp;color=fb7700" alt="ZoneTree.Analytics on NuGet" /></a>
  <a href="https://github.com/ZoneTree/ZoneTree.Analytics/tree/main/src"><img src="https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512bd4?style=flat-square" alt="Targets .NET 8, 9, and 10" /></a>
  <a href="https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-488968?style=flat-square" alt="MIT license" /></a>
</p>

<p align="center">
  <a href="#quick-start">Quick start</a> ·
  <a href="https://analytics.zonetree.dev/docs/">Documentation</a> ·
  <a href="#a-dashboard-you-can-host">Dashboard</a> ·
  <a href="#measured-performance">Benchmarks</a> ·
  <a href="https://github.com/ZoneTree/ZoneTree.Analytics/tree/main/examples/MinimalHost">Example app</a>
</p>

ZoneTree.Analytics is an **embedded analytics engine** built on [ZoneTree](https://github.com/ZoneTree/ZoneTree). Record events, keep the dataset on your infrastructure, and query it directly from C#. The core library runs in your process and requires no external database or separate analytics service.

Use it for a website's first page view, a product's conversion funnel, or a worker's processing times. Add the optional ASP.NET Core integration for request tracking, browser collection, and a reporting API, or explore the data with the included dashboard.

## What you can build

| Use case | What you can measure |
| --- | --- |
| **Website analytics** | Visitors, page views, sessions, bounce rate, popular pages, sources, campaigns, browsers, and devices |
| **Product analytics** | Signups, purchases, revenue, behavioral segments, and conversion funnels |
| **Service telemetry** | Event counts, durations, percentiles, histograms, error rates, and business metrics |
| **Multi-site reporting** | Site-scoped events and reports for multiple websites, applications, or tenants in one engine |

- **One query model.** Combine metrics, filters, dimensions, formulas, time buckets, and period comparisons through a typed C# API.
- **Pre-aggregation for recurring reports.** Read matching rollups and optional indexes; fall back to retained event history for queries that need it. Plans expose coverage and warnings.
- **Privacy-conscious defaults.** Cookie-free collection, daily anonymous identity rotation, and no stored raw IP addresses or user-agent strings.
- **Application-owned storage.** Background ingestion, configurable retention, and persistent ZoneTree stores. The core library's only direct package dependency is ZoneTree.
- **Room to customize.** Plug in event filters, enrichers, bot detectors, a user-agent parser, or a geolocation provider. Bot traffic can be excluded or reported separately.

## Quick start

The library targets **.NET 8, 9, and 10**. In a .NET console project, install:

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

Run with `dotnet run`. On an empty dataset, this records one page view and revenue of 49.00, formatted using your current culture. The data remains in `data/analytics` between runs.

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

For browser collection, map `app.MapZoneTreeAnalytics()` and load `/_analytics/script.js` on your pages. Choose either server middleware or browser tracking for each page view to avoid double counting.

To expose reports, map `app.MapZoneTreeAnalyticsApi()`. In a host with authentication and authorization configured, protect the route group with `app.MapZoneTreeAnalyticsApi().RequireAuthorization()`. Multi-site applications should also configure `AuthorizeSite` for site-level access.

See the [ASP.NET Core guide][aspnet] for collection choices, proxy configuration, and reporting access, or run the [minimal example host][minimal-host] from this repository:

```sh
dotnet run --project examples/MinimalHost
```

## A dashboard you can host

Explore traffic, acquisition, audience, events, funnels, and bot activity. Use the query explorer for custom questions and export reports from the interface. The dashboard includes a .NET server and a static UI built with PureStack and Regor.

[![ZoneTree Analytics dashboard showing visitor trends, page views, sessions, and acquisition reports](https://raw.githubusercontent.com/ZoneTree/ZoneTree.Analytics/main/docs-site/content/docs/getting-started/dashboard/dashboard-overview.png)][dashboard]

From the repository root, install the workspace dependencies:

```sh
yarn install
```

Then, from the repository root, start the server with sample data:

```sh
yarn dashboard --Analytics:SeedDemo=true
```

Open the setup link printed at the end of the server's console output and finish the setup wizard. Development mode starts the UI automatically. Demo seeding populates the empty primary site once setup is complete; omit the flag to use your own data. The core library does not require Node.js or Yarn.

Read the [dashboard guide][dashboard] for prerequisites, static release builds, and deployment settings.

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

Explore [querying][querying], [segments and funnels][funnels], and the [query reference][query-reference] for supported combinations and metric semantics.

## Measured performance

The repository includes a [documented benchmark][benchmarks] over **5 million generated events** spanning 365 days. It measures annual reports with previous-period comparisons enabled.

| Workload | Median | p95 |
| --- | ---: | ---: |
| Complete overview: six reports, up to three requests in flight | **33.3 ms** | 35.5 ms |
| Country breakdown filtered to GitHub traffic | **5.7 ms** | 6.5 ms |

Measured over 30 warm loads per scope on an Intel Core Ultra 7 265KF with 64 GiB RAM, Windows x64, and a .NET 10.0.6 Release host. Times include loopback HTTP response transfer and JSON parsing; chart rendering is excluded. The dataset has low group cardinality, and filtered reports use configured covering rollup indexes. These figures describe that workload, rather than a latency guarantee.

Matching rollups answer recurring reports from aggregates. Segments, funnels, and unsupported aggregate combinations can require event scans. The [benchmark report][benchmarks] includes configuration, query plans, individual timings, and methodology; the [benchmarking guide][benchmarking] explains how to measure your workload.

## Privacy and data ownership

The default identity strategy recognizes anonymous visitors within a UTC day using a rotating hash. Explicit visitor IDs are keyed hashes and remain stable across days. Raw IP addresses and user-agent strings are used in memory for identity and enrichment, then discarded. Page query strings are excluded by default.

Custom dimensions retain the values your application supplies, so choose what to collect. Configure dimension allow and deny lists, geography precision, identity strategy, and retention to fit your use case. For geography, the engine downloads and updates a free IP location database: add `IpLocationSources.UserCountry` for countries or `IpLocationSources.DbIpCity` for regions and cities to `Web.IpLocation.Sources`.

Visitor erasure removes an explicit visitor's retained raw events, dimension-index copies, and active session state. **Existing aggregate rollups are not recomputed.** Read [privacy and identity][privacy] and [retention and erasure][retention] for the full behavior.

## Operating the engine

| Concern | What to know |
| --- | --- |
| **Storage ownership** | One engine exclusively owns a data directory, including across processes. Keep it on persistent storage. Replicas with separate directories have separate datasets. |
| **Ingestion pressure** | The queue is bounded. `Track` drops events on a full queue by default; `TrackAsync` waits asynchronously for space. Both return whether the event was accepted. Monitor dropped-event statistics and configure request tracking's queue behavior as needed. |
| **Freshness and durability** | Aggregates are buffered until a successful flush. A crash can lose unflushed aggregates; persistent records follow ZoneTree's write-ahead log guarantees. Await clean shutdown. |
| **History** | Raw events default to 90 days; hourly rollups to 400 days; daily rollups are kept indefinitely. Hour and Day are the default enabled rollup tiers. |
| **Coverage and counts** | Inspect query plans and warnings when history or aggregates cannot cover a request. At default precision, unique counts are exact up to 4,096 identities per row, then have about 0.8% standard error. |

Use `analytics.GetStatistics()` and host logging to monitor the engine. See [deployment and lifecycle][deployment] for scaling and backups, and [time and accuracy][accuracy] for identity, count, and reporting semantics.

## Documentation

| Start here | Go deeper |
| --- | --- |
| [Getting started][getting-started] | [Architecture][architecture] |
| [ASP.NET Core integration][aspnet] | [Configuration reference][configuration] |
| [Web reports][web-reports] | [Rollups and query planning][rollups] |
| [Product analytics][product] | [Secondary indexes][indexes] |
| [Service telemetry][telemetry] | [HTTP API reference][http-api] |
| [Sites and tenants][tenants] | [Extensibility][extensibility] |
| [Deployment and lifecycle][deployment] | |

## Build and contribute

The solution's tests target .NET 10. To work on the core library alone:

```sh
dotnet build src/ZoneTree.Analytics.sln --configuration Release
dotnet test src/ZoneTree.Analytics.sln --configuration Release --no-build
```

The library also targets .NET 8 and 9; building all targets requires their targeting packs.

For repository-wide development, use **Node.js 22.17 or newer** and Corepack. The root package pins Yarn 4.18.1 and manages the dashboard and documentation as workspaces with one lockfile. Run these commands from the repository root:

```sh
corepack enable
yarn install
yarn check
```

| Command | Purpose |
| --- | --- |
| `yarn check` | Check shared dependencies, lockfile deduplication, lint, formatting, and TypeScript across the repository |
| `yarn lint` / `yarn lint:fix` | Check Biome-supported files or apply safe lint and import-order fixes |
| `yarn format:check` / `yarn format` | Check or apply the shared formatting style |
| `yarn build` | Build the .NET solution, dashboard, and documentation site |
| `yarn test` | Run repository script tests, dashboard tests, browser tracker tests, and .NET tests |
| `yarn test:web` | Run all TypeScript tests with Vitest |
| `yarn vitest` | Run all TypeScript tests in watch mode |
| `yarn dashboard` | Run the .NET dashboard app with its reporting API and UI live reload |
| `yarn dashboard:dev` | Start the dashboard UI with demo reports |
| `yarn docs:dev` | Start the documentation development server |

`yarn test:web` runs every TypeScript test through the root Vitest configuration. The browser tests require Chrome and a built dashboard (`yarn workspace zonetree-analytics-dashboard build`); the live tracker test also requires a running .NET dashboard (`yarn dashboard`). Set `ZT_DASHBOARD_TEST_URL` if the host uses a URL other than `http://127.0.0.1:5088`. Vitest's file filters and `--project unit` option are available directly when you want a smaller test run. The documentation build requires the local legal notices described in [docs-site/README.md](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/docs-site/README.md).

After changing dependencies, run `yarn dedupe` and `yarn check-dependencies`. Shared declarations must match and resolve to one installed copy. Intentional differences need a reason in the root manifest's `dependencyConsistency.exceptions`. See the dashboard [application guide][dashboard] for additional frontend commands.

Bug reports and focused pull requests are welcome. For a reproducible report, include the package version, runtime, relevant options, and a small example in a [GitHub issue](https://github.com/ZoneTree/ZoneTree.Analytics/issues).

## License

Released under the [MIT license](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/LICENSE). Built on [ZoneTree](https://github.com/ZoneTree/ZoneTree).

[getting-started]: https://analytics.zonetree.dev/docs/getting-started/
[aspnet]: https://analytics.zonetree.dev/docs/getting-started/aspnet-core/
[minimal-host]: https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/examples/MinimalHost/README.md
[dashboard]: https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/apps/ZoneTree.Analytics.Dashboard/README.md
[querying]: https://analytics.zonetree.dev/docs/guides/querying/
[funnels]: https://analytics.zonetree.dev/docs/guides/segments-and-funnels/
[query-reference]: https://analytics.zonetree.dev/docs/reference/query-model/
[benchmarks]: https://analytics.zonetree.dev/docs/performance/query-benchmarks/
[benchmarking]: https://analytics.zonetree.dev/docs/performance/benchmarking/
[privacy]: https://analytics.zonetree.dev/docs/concepts/privacy/
[retention]: https://analytics.zonetree.dev/docs/operations/retention-and-erasure/
[deployment]: https://analytics.zonetree.dev/docs/operations/deployment/
[accuracy]: https://analytics.zonetree.dev/docs/concepts/time-and-accuracy/
[architecture]: https://analytics.zonetree.dev/docs/concepts/architecture/
[configuration]: https://analytics.zonetree.dev/docs/reference/configuration/
[web-reports]: https://analytics.zonetree.dev/docs/guides/web-reports/
[rollups]: https://analytics.zonetree.dev/docs/performance/rollups/
[product]: https://analytics.zonetree.dev/docs/guides/product-analytics/
[indexes]: https://analytics.zonetree.dev/docs/performance/secondary-indexes/
[telemetry]: https://analytics.zonetree.dev/docs/guides/telemetry/
[http-api]: https://analytics.zonetree.dev/docs/reference/http-api/
[tenants]: https://analytics.zonetree.dev/docs/guides/sites-and-tenants/
[extensibility]: https://analytics.zonetree.dev/docs/guides/extensibility/
