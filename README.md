# ZoneTree.Analytics

**Embedded, privacy-conscious analytics for .NET.**

ZoneTree.Analytics is a lightweight analytics engine that runs inside your application. It records page views, sessions, custom events, telemetry and business metrics, and answers dashboard queries in milliseconds, with no analytics server, no external database and no dependency other than [ZoneTree](https://github.com/ZoneTree/ZoneTree).

[![NuGet](https://img.shields.io/nuget/v/ZoneTree.Analytics.svg)](https://www.nuget.org/packages/ZoneTree.Analytics/)
![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Platform](https://img.shields.io/badge/platform-.NET%208%20%7C%209%20%7C%2010-blue.svg)

```csharp
using var analytics = AnalyticsEngine.Open("data/analytics");

analytics.Track(AnalyticsEvent.PageView(
    url: "https://example.com/pricing?utm_source=newsletter",
    referrer: "https://www.google.com/",
    ipAddress: "203.0.113.7",
    userAgent: request.Headers.UserAgent));

var summary = analytics.Web.GetSummary(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);
// Visitors, PageViews, Sessions, BounceRate, AverageSessionDuration, PageViewsPerSession
```

---

## Features

* **Web analytics out of the box.** Page views, unique visitors, sessions, bounce rate, session duration, entry and exit pages, traffic sources and channels, referrers, UTM campaigns, countries, regions, cities, browsers, operating systems and devices.
* **Generic events and metrics.** Any event name, custom dimensions, numeric metrics with sum, count, min, max and average. Use it for product analytics, application telemetry or business KPIs.
* **Fast queries.** Time series (minute to year), breakdowns, filters and top-N lists over pre-aggregated rollups, typically in single-digit milliseconds. Ad-hoc dimension combinations fall back to raw event scans automatically.
* **Accurate unique counts.** Mergeable HyperLogLog sketches are exact up to 4,096 visitors per row and have about 0.8% error beyond that. Uniques are de-duplicated correctly across any time range.
* **Privacy by design.** IP addresses and user agents are never stored. Anonymous visitors are identified with a daily-rotating salted hash, so no cookies are needed. Geo precision, query strings, user agent details and custom dimensions are all configurable, and raw events of a visitor can be erased on request.
* **Embedded and self-contained.** One `AnalyticsEngine` per data directory, thread-safe, with background ingestion that never blocks request threads.
* **Reliable.** Write-ahead logging, crash recovery, safe restarts that resume active sessions, and configurable retention per data tier.
* **Scales with your data.** Time-partitioned ZoneTree storage keeps memory bounded. Retention drops whole partitions, and ingestion can run on several workers.
* **Multi-site.** One engine can serve many sites, apps or tenants with isolated data.

---

## Install

```bash
dotnet add package ZoneTree.Analytics
```

Targets .NET 8, 9 and 10.

---

## Quick start

### Create one engine for the application lifetime

```csharp
using ZoneTree.Analytics;

var analytics = new AnalyticsEngine(new AnalyticsOptions
{
    DataDirectory = "data/analytics",
    TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"),
});

// On shutdown: queued events are processed and everything is flushed.
analytics.Dispose();
```

### Track page views in ASP.NET Core

The optional `ZoneTree.Analytics.AspNetCore` package registers the engine, tracks page views and serves a cookie-less JavaScript tracker:

```bash
dotnet add package ZoneTree.Analytics.AspNetCore
```

```csharp
builder.Services.AddZoneTreeAnalytics(
    options => options.DataDirectory = "data/analytics",
    web => web.VisitorIdResolver = context => context.User.FindFirst("sub")?.Value);

var app = builder.Build();
app.UseForwardedHeaders();      // so the client IP is correct behind a proxy

// Option 1: track HTML page views on the server.
app.UseZoneTreeAnalytics();

// Option 2: serve /_analytics/script.js and the /_analytics/event beacon endpoint.
app.MapZoneTreeAnalytics();
```

```html
<script defer src="/_analytics/script.js"></script>
<script>
  window.zta = window.zta || function () { (zta.q = zta.q || []).push(arguments) };
  zta('signup', { plan: 'pro' }, { value: 49 });
</script>
```

Use one of the two options for page views, not both. The tracker follows single-page-app navigation (`history.pushState`), uses `sendBeacon`, sets no cookies and ignores localhost unless `data-allow-localhost="true"` is set. The engine is a DI singleton that is flushed and disposed on shutdown.

### Track page views without the package

```csharp
analytics.Track(AnalyticsEvent.PageView(
    url: request.GetDisplayUrl(),
    referrer: request.Headers.Referer,
    ipAddress: context.Connection.RemoteIpAddress?.ToString(),
    userAgent: request.Headers.UserAgent));
```

`Track` only enqueues the event and returns immediately. Enrichment, session tracking, storage and aggregation happen on background workers.

### Track custom events and metrics

```csharp
analytics.Track(new AnalyticsEvent("purchase")
{
    VisitorId = user.Id,                       // optional; stored only as a keyed hash
    Url = "https://shop.example.com/checkout", // optional page context
}
.WithDimension("plan", "pro")
.WithMetric("revenue", 49.0));

// Telemetry: no visitor, no page.
analytics.Track(new AnalyticsEvent("job_completed")
    .WithDimension("queue", "emails")
    .WithMetric("duration_ms", 812));
```

### Read reports

```csharp
var from = DateTime.UtcNow.AddDays(-30);
var to = DateTime.UtcNow;

var summary   = analytics.Web.GetSummary(from, to);
var daily     = analytics.Web.GetTimeseries(from, to, TimeInterval.Day);
var pages     = analytics.Web.GetTopPages(from, to, limit: 10);
var exits     = analytics.Web.GetExitPages(from, to);
var entries   = analytics.Web.GetEntryPages(from, to);
var sources   = analytics.Web.GetSources(from, to);
var channels  = analytics.Web.GetChannels(from, to);
var campaigns = analytics.Web.GetCampaigns(StandardDimensions.UtmCampaign, from, to);
var countries = analytics.Web.GetCountries(from, to);
var devices   = analytics.Web.GetDevices(from, to);
var events    = analytics.Web.GetCustomEvents(from, to);
var live      = analytics.Web.GetCurrentVisitors(); // last 5 minutes, real-time
```

Every report accepts filters:

```csharp
var germanMobile = analytics.Web.GetSummary(from, to, filters:
[
    DimensionFilter.Equal(StandardDimensions.Country, "DE"),
    DimensionFilter.Equal(StandardDimensions.Device, "Mobile"),
]);
```

---

## Queries

`AnalyticsEngine.Query` is the general query API. A query selects one event, a time range, an optional time series interval, group-by dimensions, filters, ordering and a limit.

```csharp
var result = analytics.Query(new AnalyticsQuery()
    .ForEvent("purchase")
    .Between(from, to)
    .Every(TimeInterval.Week)
    .GroupBy("plan")
    .Where(StandardDimensions.Country, "DE")
    .Order(QueryOrder.ByMetricSum("revenue"))
    .Top(10));

foreach (var row in result.Rows)
{
    Console.WriteLine($"{row.Bucket:yyyy-MM-dd} {row.Dimensions[0]}: " +
        $"{row.Events} purchases, {row.Visitors} buyers, revenue {row.Sum("revenue")}");
}

Console.WriteLine($"Total buyers in range: {result.Totals.Visitors}");
```

Each row has:

| Member | Meaning |
| --- | --- |
| `Bucket` | Start of the time bucket (null without an interval). Minute and hour buckets are UTC; day and coarser buckets carry the reporting time zone offset. |
| `Dimensions` | Group-by values in query order. An empty string is a known empty value, for example direct traffic has an empty referrer. |
| `Events` | Number of events. For `$session` rows, the number of sessions. |
| `Visitors` | Unique visitors. |
| `Metrics` | `MetricValue` per metric: `Sum`, `Count`, `Min`, `Max`, `Average`. |

`result.Totals` aggregates every matching row before the limit, with unique visitors de-duplicated across rows and buckets.

### Filters

`Equal`, `NotEqual`, `Contains`, `NotContains`, `StartsWith` and `EndsWith`, each with one or more values and optional `IgnoreCase`. All filters must match. A filter only matches events that carry the dimension, including for negative operators.

### Where answers come from

Ingestion maintains **rollups**: pre-aggregated rows per time bucket for configured dimension sets. A query is answered from rollups when the union of its group-by and filter dimensions equals a rollup's dimension set. The time range is covered greedily: whole days from daily rollups, remaining hours from hourly rollups, remaining minutes from minute rollups, and any remainder from raw events.

Queries over other dimension combinations, such as "pages where country = DE and device = Mobile", are answered by scanning **raw events**, as long as raw events are stored and retained. `AnalyticsResult.UsedRollups` and `UsedRawEvents` tell you which path was taken. You can force one with `Source = QuerySource.Rollups` or `QuerySource.RawEvents`; both return the same numbers.

Add rollups for hot ad-hoc combinations:

```csharp
options.Rollups.Definitions.Add(new RollupDefinition(StandardDimensions.Country, StandardDimensions.Page));
options.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] });
```

A rollup added to an existing data directory covers data from that moment on; earlier ranges keep being answered from raw events.

---

## Web analytics model

### Standard dimensions

| Dimension | Source |
| --- | --- |
| `page`, `hostname` | Page URL. Query strings are removed by default; UTM parameters are extracted. |
| `entry_page` | First page of the session. |
| `source` | `utm_source`, a known referrer name ("Google", "Twitter", "Hacker News", "ChatGPT", ...), the referrer host, or "Direct". |
| `referrer` | Referrer host, empty for direct traffic. |
| `channel` | Direct, Organic Search, Paid Search, Organic Social, Paid Social, Email, Affiliates, AI Assistants, Video, Referral or Paid Other. |
| `utm_source`, `utm_medium`, `utm_campaign`, `utm_term`, `utm_content` | Campaign parameters of the landing URL. |
| `country`, `region`, `city` | Your `IGeoLocator`, or values you pass as dimensions (for example from a CDN header). |
| `browser`, `os`, `device` (+ `browser_version`, `os_version`) | Built-in, dependency-free user agent parser. |

Source, campaign, geography and device are **session-level**: they are captured from the first event of a session and attached to every event of the session. Every page view of a visit is therefore attributed to the same source and country.

Any standard dimension can be overridden per event:

```csharp
analytics.Track(AnalyticsEvent.PageView(url, referrer, ip, ua)
    .WithDimension(StandardDimensions.Country, request.Headers["CF-IPCountry"]));
```

### Sessions

A session is a sequence of events from one visitor with no gap longer than `Sessions.Timeout` (30 minutes by default). Session metrics live on the engine-generated `$session` event:

| Metric | Meaning |
| --- | --- |
| `Events` of a `$session` row | Sessions started. |
| `$bounces` | Sessions with at most one interactive event. Page views and custom events are interactive unless `NonInteractive` is set. |
| `$duration` | Total session time in seconds, from the first to the last event. |
| `$pageviews` | Page views inside sessions. |

Page view rows carry `$entrances` (sessions that started on the page) and `$exits` (sessions whose last page view was on the page).

```csharp
var bySource = analytics.Query(new AnalyticsQuery()
    .ForEvent(StandardEvents.Session)
    .Between(from, to)
    .GroupBy(StandardDimensions.Source));

foreach (var row in bySource.Rows)
{
    var bounceRate = row.Sum(StandardMetrics.Bounces) / row.Events;
    var avgSeconds = row.Sum(StandardMetrics.Duration) / row.Events;
}
```

Session metrics are maintained incrementally as additive deltas, so they are real-time: there is no session-close batch job.

---

## Privacy

ZoneTree.Analytics is designed to work without cookies and without storing personal data.

* **No IP addresses or user agents are stored.** They are used in memory for visitor hashing, geolocation and device detection, then discarded.
* **Visitor identification** is configurable through `Privacy.VisitorIdentification`:
  * `DailyRotatingHash` (default): anonymous visitors are identified by HMAC-SHA256 of site, IP and user agent with a random salt that rotates every UTC day and is then destroyed. Visitors cannot be followed across days, and hashes cannot be recomputed after rotation. Explicit `VisitorId` values (for example a user id) are hashed with a persistent secret.
  * `PersistentHash`: anonymous visitors are recognized across days. Less private.
  * `ExplicitOnly`: only explicit `VisitorId` values identify visitors.
  * `None`: no visitor tracking, no sessions or unique counts.
* **Secret management.** Supply `Privacy.VisitorIdSecret` from your secret store to keep the hashing key away from the data files.
* **Data minimization.** `GeoPrecision` (none, country, region, city), `CollectUserAgentDetails`, `CollectVersions`, `IncludeQueryString` (off by default), `AllowedDimensions`, `DeniedDimensions`, `MaxCustomDimensions` and `MaxValueLength`.
* **Filtering.** `EventFilter` can drop events before processing, for example internal traffic or visitors who opted out. Bots, crawlers, monitors and HTTP libraries are excluded by default.
* **Retention** per data tier (see below).
* **Erasure.** `await analytics.EraseVisitorAsync(userId)` deletes a visitor's raw events and session. Rollups contain only aggregates and sketches, never identifiers.

Note that daily rotation means a person who visits on several days counts as several visitors in multi-day totals. Use `PersistentHash` or explicit visitor ids if you need cross-day unique visitors.

---

## Configuration

```csharp
var options = new AnalyticsOptions
{
    DataDirectory = "data/analytics",
    DefaultSite = "example.com",
    TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"),

    Ingestion =
    {
        WorkerCount = 2,                          // events are routed to workers by visitor
        QueueCapacity = 100_000,                  // per worker
        QueueFullBehavior = QueueFullBehavior.Drop,
        FlushInterval = TimeSpan.FromSeconds(2),
    },
    Sessions = { Timeout = TimeSpan.FromMinutes(30) },
    Rollups =
    {
        Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day,
        HyperLogLogPrecision = 14,
    },
    Retention =
    {
        RawEvents = TimeSpan.FromDays(90),
        MinuteRollups = TimeSpan.FromDays(2),
        HourRollups = TimeSpan.FromDays(400),
        DayRollups = null,                        // keep forever
    },
    Privacy = { GeoPrecision = GeoPrecision.Country },
    Web =
    {
        GeoLocator = myGeoLocator,                // e.g. a MaxMind reader adapter
        PathNormalizer = path => Regex.Replace(path, @"/\d+", "/:id"),
        ReferrerSources = new Dictionary<string, string> { ["lobste.rs"] = "Lobsters" },
    },
    Storage =
    {
        StoreRawEvents = true,
        RawEventPartitioning = PartitionPeriod.Month,
        WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed,
    },
};
```

The time zone, sketch precision and raw event partitioning define the storage format. They are persisted on creation and validated when the directory is reopened.

---

## How it uses ZoneTree

ZoneTree is an ordered, persistent LSM key-value engine. ZoneTree.Analytics is a data model on top of it:

```text
data/analytics/
  meta/                       configuration, secrets, daily salts
  sessions/                   active sessions, keyed by visitor hash (TTL deletion)
  events/2026-10/             raw events, one ZoneTree per month (or day)
  rollups/day/2026/           daily rollups, one ZoneTree per year
  rollups/hour/2026-10/       hourly rollups, one ZoneTree per month
  rollups/minute/2026-10-08/  minute rollups, one ZoneTree per day
```

* **Order-preserving binary keys.** Rollup keys are `site · event · dimension-set · bucket · values`, with zero-terminated segments and big-endian timestamps. Every breakdown or time series is a single contiguous ZoneTree range scan, whatever the number of buckets or values. Raw event keys are `site · timestamp · sequence`, so a site's time range is contiguous too.
* **Pre-aggregation with atomic merges.** Workers aggregate in memory and periodically merge rows into ZoneTree with `TryAtomicAddOrUpdate`. Values are immutable: every merge creates a new value, so concurrent readers never see partial updates.
* **Time partitioning.** Each period is its own ZoneTree with its own maintainer. Partitions open on demand, idle ones are merged to disk and closed, and retention drops whole partitions with `Maintenance.Drop()`: no tombstones and no compaction debt.
* **Sessions with TTL.** The session store uses ZoneTree's deletion predicate as a time-to-live, so expired sessions disappear from reads and are physically removed by normal merges.
* **Bounded memory.** Memory is governed by ZoneTree's mutable segment limits per open partition, not by the amount of stored data.
* **Durability.** Every store uses ZoneTree's write-ahead log (async compressed by default; the metadata store uses the synchronous WAL).

---

## Performance

A synthetic benchmark is included in `src/ZoneTree.Analytics.Playground`:

```bash
dotnet run -c Release --project src/ZoneTree.Analytics.Playground -- 1000000 30 1
```

It generates one million page views over 30 days (50,000 recurring visitors, 12 pages, 12 referrers, 7 user agents including a bot) with raw events, sessions and the default rollups enabled. This is a worst case for ingestion: almost every event starts a new session. Results on an Intel Core Ultra 7 265KF with a desktop hard disk:

| Measure | Result |
| --- | ---: |
| Ingestion, 1 worker | ~80,000 events/s |
| Ingestion, 4 workers | ~145,000 events/s |
| 30-day summary | 0.7 ms |
| 30-day daily time series | 1.2 ms |
| 7-day hourly time series | 1.2 ms |
| 30-day top pages / sources / countries | 2–3 ms |
| Unaligned range (rollups plus raw edges) | ~15 ms |
| Ad-hoc 2-filter breakdown, 1 day of raw events (~28k events) | ~40 ms |
| Ad-hoc 2-filter breakdown, 30 days of raw events (~860k events) | ~0.8 s |
| Disk usage | ~120 bytes per event, rollups included |

Rollup query time depends on the number of rows in the range, not on the number of events.

---

## Operations

* **Flushing.** Rollup-based answers lag ingestion by at most `Ingestion.FlushInterval`. Call `await analytics.FlushAsync()` when a result must include everything tracked so far. Raw events, and therefore `GetCurrentVisitors`, are real-time.
* **Durability window.** After a crash, aggregates buffered since the last flush (2 seconds by default) are lost, and recent WAL records follow ZoneTree's WAL mode guarantees. A graceful `Dispose` processes the queue and flushes everything.
* **Statistics.** `analytics.GetStatistics()` reports accepted, dropped, processed, rejected, filtered and bot events, sessions, flushes, errors, queue length and open partitions.
* **Logging.** Pass `Storage.Logger` (a ZoneTree `ILogger`) to route engine and storage logs into your logging system.
* **Backups.** Every partition is a regular ZoneTree directory. For a consistent offline backup, dispose the engine and copy the data directory.
* **Retention** runs hourly by default (`Retention.CheckInterval`) and can be triggered with `ApplyRetention()`.

---

## Roadmap

* A JSON reporting API and an embeddable dashboard for ASP.NET Core.
* Rebuilding rollups from raw events, for example after adding a rollup definition.
* Percentile metrics.
* Live backup across all partitions.

---

## License

MIT. See [LICENSE](LICENSE).
