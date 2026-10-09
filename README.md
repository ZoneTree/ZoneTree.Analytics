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

var summary = await analytics.Web.GetSummaryAsync(new WebReportRequest { Range = DateRange.LastDays(30) });
// Visitors, PageViews, Sessions, BounceRate, AverageSessionDuration, PageViewsPerSession
```

---

## Features

* **Web analytics out of the box.** Visitors, page views, sessions, bounce rate, session duration, entry and exit pages, sources, channels, referrers, UTM campaigns, geography, browsers, operating systems and devices.
* **Generic events and metrics.** Any event name with custom dimensions and numeric values: product analytics, telemetry and business KPIs.
* **One query model.** Metrics such as visitors, bounce rate, revenue and conversion rate in one query, grouped and filtered, as totals or time series from minute to year, compared with a previous period.
* **Behavioral segments and funnels.** "Visitors who signed up", "sessions that viewed /pricing", and step-by-step conversion funnels.
* **Fast.** Pre-aggregated rollups answer typical dashboard queries in single-digit milliseconds; ad-hoc combinations fall back to raw events automatically.
* **Accurate unique counts.** Exact up to 4,096 visitors per row, about 0.8% error beyond, correctly de-duplicated across any range.
* **Privacy by design.** No cookies, no stored IP addresses or user agents, daily-rotating visitor hashes, configurable data minimization and retention, visitor erasure.
* **Embedded and reliable.** One thread-safe engine per data directory, background ingestion that never blocks request threads, write-ahead logging, crash recovery and bounded memory.
* **Bot traffic, kept apart.** Search engines, AI crawlers, SEO tools, link previews, monitors and scripts are recognized and either excluded or reported next to page views, by bot and category.
* **Extensible.** Plug in event filters, enrichers, bot detectors, a user agent parser and a geolocator through one consistent model.
* **Multi-site.** One engine serves many sites, apps or tenants with isolated data.

---

## Install

```bash
dotnet add package ZoneTree.Analytics
dotnet add package ZoneTree.Analytics.AspNetCore   # optional: middleware, tracker, reporting API
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
await analytics.DisposeAsync();
```

### Track events

```csharp
// A page view. Browser, OS, device, source, channel, UTM and geography are derived from it.
analytics.Track(AnalyticsEvent.PageView(url, referrer, ipAddress, userAgent));

// A custom event with dimensions and values.
analytics.Track(new AnalyticsEvent("purchase")
{
    VisitorId = user.Id,                     // optional; only a keyed hash is stored
    Url = "https://example.com/checkout",    // optional page context
    Dimensions = { ["plan"] = "pro" },
    Metrics = { ["revenue"] = 49.0 },
});

// Telemetry without a visitor or page.
analytics.Track(new AnalyticsEvent("job_completed")
    .WithDimension("queue", "emails")
    .WithMetric("duration_ms", 812));
```

`Track` validates the event, copies it, queues it and returns immediately, so the instance can be reused. Enrichment, session tracking, storage and aggregation happen on background workers. Event names starting with `$` are reserved.

### Read reports

```csharp
var request = new WebReportRequest
{
    Range = DateRange.LastDays(30),
    Compare = Comparison.PreviousPeriod,
};

WebSummary summary = await analytics.Web.GetSummaryAsync(request);
var daily     = await analytics.Web.GetTimeseriesAsync(request, TimeInterval.Day);
var pages     = await analytics.Web.GetPagesAsync(request);       // views, visitors, entrances, exits
var exitPages = await analytics.Web.GetExitPagesAsync(request);
var sources   = await analytics.Web.GetBreakdownAsync(request, StandardDimensions.Source);
var countries = await analytics.Web.GetBreakdownAsync(request, StandardDimensions.Country);
var goals     = await analytics.Web.GetGoalsAsync(request);       // custom events and conversion rates
long? live    = analytics.Web.GetCurrentVisitors();               // last 5 minutes, real-time

Console.WriteLine($"{summary.Visitors} visitors (previously {summary.Previous.Visitors})");
```

Every report takes filters and segments:

```csharp
var germanMobile = new WebReportRequest
{
    Filters =
    {
        Filter.Equal(StandardDimensions.Country, "DE"),
        Filter.Equal(StandardDimensions.Device, "Mobile"),
    },
};
var buyers = new WebReportRequest { Segments = { Segment.VisitorsWho("purchase") } };
```

All web reports count visitors the same way (unique visitors with a page view), so numbers add up across reports. Session metrics are reported for session-level breakdowns (source, campaign, geography, device, entry page) and are null otherwise.

---

## Queries

`QueryAsync` (and its synchronous twin `Query`) is the general query API.

```csharp
var result = await analytics.QueryAsync(new AnalyticsQuery
{
    Range = DateRange.LastDays(30),
    GroupBy = { StandardDimensions.Source },
    Metrics = { Metric.Visitors, Metric.BounceRate, Metric.ConversionRate("purchase") },
    OrderBy = { Order.By(Metric.Visitors) },
    Limit = 10,
});

foreach (var row in result.Rows)
    Console.WriteLine($"{row[StandardDimensions.Source]}: {row[Metric.Visitors]} visitors, " +
        $"{row[Metric.BounceRate]:P0} bounce, {row[Metric.ConversionRate("purchase")]:P1} converted");

Console.WriteLine($"{result.Totals[Metric.Visitors]} visitors in total");
```

A query has a base `Event` (page views by default), a `Range`, an optional `Interval`, `GroupBy` dimensions, `Filters`, `Segments`, `Metrics`, `OrderBy`, `Limit`, `Offset` and an optional `Compare`.

### Metrics

| Metric | Reads | Meaning |
| --- | --- | --- |
| `Events`, `Visitors` | base event | count and unique visitors |
| `EventsOf(e)`, `VisitorsOf(e)` | event `e` | count and unique visitors of another event |
| `PageViews` | page views | page view count |
| `Sessions`, `Bounces`, `BounceRate`, `SessionDuration`, `AverageSessionDuration`, `PageViewsPerSession` | sessions | session metrics; rates are 0 to 1, durations are seconds |
| `Entrances`, `Exits`, `ExitRate` | page views | sessions that started or ended on a page |
| `Sum(v)`, `Average(v)`, `Min(v)`, `Max(v)`, `Count(v)` | base event, or `Sum(v, e)` | aggregates of a tracked value |
| `Conversions(goal)`, `ConversionRate(goal)` | goal event | goal visitors, and goal visitors ÷ base visitors |
| `PercentOfTotal(m)` | as `m` | each row's share of the total |
| `BotHits` | bot events | bot requests, see [Bot traffic](#bot-traffic) |

The engine runs one sub-query per event the metrics read and joins them on the time bucket and group values. A row is returned for every group that has data for any metric; set `IncludeRowsWithoutBaseEvent = false` to keep only groups with base events. Filters apply to every sub-query.

Session metrics can only be grouped and filtered by session-level dimensions and the entry page; entrances and exits also by page and hostname. Use a segment to select sessions by anything else. Bot metrics can only be grouped and filtered by page, hostname, `event`, `bot` and `bot_category`, and cannot be combined with segments.

Every metric has a stable key, for example `visitors`, `bounce_rate`, `sum:revenue`, `avg:duration_ms@job_completed`, `conversion_rate:signup` or `percent:visitors`; `Metric.Parse` turns keys back into metrics.

### Results

```csharp
row.Bucket                          // DateTimeOffset? bucket start
row[StandardDimensions.Page]        // group value; null when the events do not carry the dimension
row[Metric.Visitors]                // metric value
row.Previous?[Metric.Visitors]      // the comparison period
row.Change(Metric.Visitors)         // relative change, e.g. 0.25 for +25%
result.Totals                       // over all rows, before the limit; visitors de-duplicated
result.TotalRows                    // for paging
result.Plan.Warnings                // e.g. part of the range is outside raw event retention
```

A dimension that an event does not carry groups under `null`, so grouped totals always equal ungrouped totals. An empty string is a known empty value, for example the referrer of direct traffic.

### Ranges, time zones and comparisons

```csharp
DateRange.Today; DateRange.Yesterday; DateRange.LastDays(7); DateRange.LastDays(7, includeToday: false);
DateRange.ThisMonth; DateRange.LastMonth; DateRange.ThisYear; DateRange.Month(2026, 9); DateRange.Year(2026);
DateRange.Days(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 14));
DateRange.Last(TimeSpan.FromMinutes(30));
DateRange.Between(from, to);

query.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
query.Compare = Comparison.PreviousPeriod;   // or PreviousYear, or Custom(range)
```

Calendar ranges and day, week, month and year buckets follow the query's time zone (the engine's reporting time zone by default). Daily rollups are stored in the reporting time zone; other time zones are answered from hourly rollups.

### Filters

```csharp
Filter.Equal("country", "DE", "AT");        Filter.NotEqual("page", "/");
Filter.Contains("page", "docs");            Filter.NotContains("page", "admin");
Filter.StartsWith("page", "/blog/");        Filter.EndsWith("page", ".html");
Filter.Matches("page", @"^/blog/\d+$");     Filter.NotMatches("referrer", "spam");
Filter.IsSet("plan");                       Filter.IsNotSet("plan");
new Filter("browser", FilterOperator.Equals, ignoreCase: true, "chrome");
Filter.Parse("country==DE|AT");             // also !=, ~=, !~=, ^=, $=, =~, !=~, dim?, !dim?
```

Positive operators never match a missing dimension; negative operators always do.

### Segments

```csharp
query.Segments.Add(Segment.VisitorsWho("purchase"));
query.Segments.Add(Segment.SessionsWith(StandardEvents.PageView, Filter.Equal("page", "/pricing")));
query.Segments.Add(Segment.VisitorsWhoViewed("/pricing").Exclude());
```

A segment restricts a query to visitors (or sessions) with at least one matching event in the range, or without one when excluded. Segments are evaluated over raw events.

### Funnels

```csharp
var funnel = await analytics.QueryFunnelAsync(new FunnelQuery
{
    Range = DateRange.LastDays(30),
    Steps =
    {
        FunnelStep.Page("Pricing", "/pricing"),
        FunnelStep.Page("Signup form", "/signup"),
        FunnelStep.Goal("signup"),
    },
    Window = TimeSpan.FromDays(1),   // null: all steps within one session
});

foreach (var step in funnel.Steps)
    Console.WriteLine($"{step.Name}: {step.Visitors} ({step.ConversionRate:P0}, drop-off {step.DropOff})");
```

### Where answers come from

Ingestion maintains **rollups**: pre-aggregated rows per time bucket for configured dimension sets. A sub-query is answered from rollups when the union of its group-by and filter dimensions equals a rollup's dimension set. The range is covered greedily: whole days from daily rollups, remaining hours from hourly rollups, remaining minutes from minute rollups, and any remainder from raw events. Other dimension combinations, segments and funnels scan **raw events** within raw event retention. `result.Plan` shows the source of each part; `Source = QuerySource.Rollups` or `QuerySource.RawEvents` forces one, and both give the same numbers.

Add rollups for hot combinations:

```csharp
options.Rollups.Definitions.Add(new RollupDefinition(StandardDimensions.Country, StandardDimensions.Page));
options.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] });
```

A rollup added to an existing data directory covers data from that moment on; earlier ranges are answered from raw events.

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
| `browser`, `os`, `device` (+ `browser_version`, `os_version`) | Built-in, dependency-free user agent parser, or your `IUserAgentParser`. |
| `bot`, `bot_category`, `event` | On bot events only: the bot, its category and the original event name. |

Source, campaign, geography and device are **session-level**: captured from the first event of a session and attached to every event of the session, so all page views and goals of a visit share them. Standard dimensions can be overridden per event, for example `Dimensions = { [StandardDimensions.Country] = cdnCountry }`.

### Sessions

A session is a sequence of events from one visitor with no gap longer than `Sessions.Timeout` (30 minutes by default). A session bounces when it has at most one interactive event; page views and custom events are interactive unless `NonInteractive` is set. Session metrics are attributed to the session start; entrances and exits to their page views. Sessions are maintained incrementally, so session metrics are real-time.

### Bot traffic

Every event goes through the bot detectors (`Web.BotDetectors`). The built-in detector recognizes bots by user agent and names them, for example `Googlebot`, `GPTBot`, `ClaudeBot`, `AhrefsBot`, `facebookexternalhit`, `UptimeRobot`, `HeadlessChrome` or `curl`, in the categories of `BotCategories`: Search, AI, SEO, Social Preview, Monitoring, Automation, Scripts, Archiver and Other.

`Web.Bots` decides what happens to bot traffic:

* `BotTraffic.Exclude` (default): bot events are discarded and counted in `BotsExcluded`.
* `BotTraffic.TrackSeparately`: bot events are stored as `$bot` events with the dimensions `bot`, `bot_category`, `event` (the original event name), `page` and `hostname`. They never count as visitors, page views, sessions or goals, and they have no visitor identity.

With `TrackSeparately`, the web reports show bot hits next to human traffic:

```csharp
options.Web.Bots = BotTraffic.TrackSeparately;

var summary = await analytics.Web.GetSummaryAsync(request);   // summary.BotHits next to summary.PageViews
var pages   = await analytics.Web.GetPagesAsync(request);     // PageStats.BotHits per page
var bots    = await analytics.Web.GetBotsAsync(request);      // name, category, hits and share

// Which pages do AI crawlers read?
var ai = await analytics.QueryAsync(new AnalyticsQuery
{
    Event = StandardEvents.Bot,
    GroupBy = { StandardDimensions.Page },
    Filters = { Filter.Equal(StandardDimensions.BotCategory, BotCategories.AI) },
    Metrics = { Metric.Events },
});
```

`BotHits` is null in web reports whose filters or segments do not apply to bots, for example a country filter. Bots that run JavaScript rarely load the tracker script, so server-side tracking with `UseZoneTreeAnalytics()` sees far more bot traffic than the script.

---

## ASP.NET Core

```csharp
builder.Services.AddZoneTreeAnalytics(
    options => options.DataDirectory = "data/analytics",
    web =>
    {
        web.VisitorIdResolver = context => context.User.FindFirst("sub")?.Value;
        web.AuthorizeSite = (context, site) => context.User.IsInRole("analytics");
    });

var app = builder.Build();
app.UseForwardedHeaders();               // so the client IP is correct behind a proxy

app.UseZoneTreeAnalytics();              // option 1: track HTML page views on the server
app.MapZoneTreeAnalytics();              // option 2: /_analytics/script.js and its beacon endpoint
app.MapZoneTreeAnalyticsApi()            // JSON reporting API under /_analytics/api
   .RequireAuthorization();
```

```html
<script defer src="/_analytics/script.js"></script>
<script>
  window.zta = window.zta || function () { (zta.q = zta.q || []).push(arguments) };
  zta('signup', { plan: 'pro' }, { value: 49 });
</script>
```

Use one of the two page view options, not both. The tracker follows single-page-app navigation, uses `sendBeacon`, sets no cookies and ignores localhost unless `data-allow-localhost="true"` is set. The engine is a DI singleton that is flushed and disposed on shutdown, and its logs go to `Microsoft.Extensions.Logging`.

### Reporting API

| Endpoint | Returns |
| --- | --- |
| `GET summary` | headline metrics |
| `GET timeseries?interval=day` | headline metrics per bucket |
| `GET breakdown?dimension=source` | rows with visitors, page views, share and session metrics |
| `GET pages`, `GET exit-pages` | page statistics |
| `GET goals` | custom events with conversion rates |
| `GET bots` | bots with category, hits and share (with `BotTraffic.TrackSeparately`) |
| `GET realtime?minutes=5` | current visitors |
| `POST query` | any query, as JSON |
| `POST funnel` | a funnel, as JSON |

Summary, time series and page rows include `botHits` (null unless bots are tracked separately). Common parameters: `site`, `range` (`today`, `yesterday`, `7d`, `30d`, `month`, `last-month`, `year`, `30m`, `12h`) or `from` and `to`, `tz`, `filter` (repeatable, `Filter.Parse` syntax), `compare` (`previous` or `year`), `limit`, `offset`.

```json
POST /_analytics/api/query
{
  "range": "30d",
  "groupBy": ["source"],
  "metrics": ["visitors", "bounce_rate", "conversion_rate:signup"],
  "filters": ["country==DE|AT"],
  "segments": [{ "scope": "visitors", "event": "signup", "exclude": true }],
  "orderBy": ["-visitors"],
  "limit": 10,
  "compare": "previous"
}
```

The API exposes analytics data: protect it with authorization, and restrict sites with `AuthorizeSite`.

---

## Privacy

* **No IP addresses or user agents are stored.** They are used in memory for visitor hashing, geolocation and device detection, then discarded.
* **Visitor identification** (`Privacy.VisitorIdentification`):
  * `DailyRotatingHash` (default): anonymous visitors are identified by HMAC-SHA256 of site, IP and user agent with a random salt that rotates every UTC day and is then destroyed, so visitors cannot be followed across days and hashes cannot be recomputed. Explicit `VisitorId` values are hashed with a persistent secret.
  * `PersistentHash`: anonymous visitors are recognized across days. Less private.
  * `ExplicitOnly`: only explicit `VisitorId` values identify visitors.
  * `None`: no visitor tracking, no sessions or unique counts.
* **Secret management.** Supply `Privacy.VisitorIdSecret` from your secret store to keep the hashing key away from the data files.
* **Data minimization.** `GeoPrecision`, `CollectUserAgentDetails`, `CollectVersions`, `IncludeQueryString` (off by default), `AllowedDimensions`, `DeniedDimensions`, `MaxCustomDimensions`, `MaxValueLength`.
* **Filtering.** `EventFilters` drop events before they are queued, for example internal traffic or visitors who opted out. Bot traffic is excluded by default.
* **Retention** per data tier, see below.
* **Erasure.** `await analytics.EraseVisitorAsync(userId)` deletes the raw events and the session of an explicitly identified visitor. Rollups contain only aggregates and sketches, never identifiers.

With daily rotation, a person who visits on several days counts once per day in multi-day totals. Use `PersistentHash` or explicit visitor ids for cross-day unique visitors.

---

## Configuration

```csharp
var options = new AnalyticsOptions
{
    DataDirectory = "data/analytics",
    DefaultSite = "example.com",
    TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"),
    Ingestion = { WorkerCount = 2, QueueFullBehavior = QueueFullBehavior.Drop },
    Sessions = { Timeout = TimeSpan.FromMinutes(30) },
    Rollups = { Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day },
    Retention =
    {
        RawEvents = TimeSpan.FromDays(90),
        MinuteRollups = TimeSpan.FromDays(2),
        HourRollups = TimeSpan.FromDays(400),
        DayRollups = null,                 // keep forever
    },
    Storage =
    {
        Partitioning = { RawEvents = PartitionPeriod.Day },  // one ZoneTree per day of raw events
    },
    Privacy = { GeoPrecision = GeoPrecision.Country },
    Web =
    {
        GeoLocator = myGeoLocator,         // e.g. a MaxMind reader adapter
        Bots = BotTraffic.TrackSeparately,
    },
    Enrichers = { EventEnricher.NormalizePage(path => Regex.Replace(path, @"/\d+", "/:id")) },
};
```

The reporting time zone, sketch precision and partitioning define the storage format. They are persisted on creation; reopening with different values throws `AnalyticsConfigurationException`. The partitioning of a tier that holds no data yet can still change.

### Partitioning

Each data tier is split over time into partitions, and each partition is a separate ZoneTree. `Storage.Partitioning` sets the period per tier:

| Tier | Option | Default | Allowed |
| --- | --- | --- | --- |
| Raw events | `RawEvents` | `Month` | `Day`, `Month`, `Year` |
| Minute rollups | `MinuteRollups` | `Day` | `Day`, `Month`, `Year` |
| Hourly rollups | `HourRollups` | `Month` | `Day`, `Month`, `Year` |
| Daily rollups | `DayRollups` | `Year` | `Month`, `Year` |

Retention drops whole partitions once they end before the cutoff, so a tier keeps data for up to one partition period longer than its retention. Smaller partitions make retention more precise and keep trees, merges and drops small; larger partitions mean fewer trees and directories, and fewer trees per long-range query. Use `Day` for raw events of high-volume sites or short raw retention. A partition period longer than the tier's retention is rejected, for example monthly minute rollups with two-day retention.

---

## Extending

Every extension point follows the same model: a single-method interface, a static class of the same name without the `I` that offers the built-in implementation and a `Create` factory for lambdas, and an option to plug it in.

| Interface | Plugged in at | Built-in | Runs | Purpose |
| --- | --- | --- | --- | --- |
| `IEventFilter` | `EventFilters` | none | on the calling thread, before queueing | drop events: internal traffic, opt-outs |
| `IBotDetector` | `Web.BotDetectors` | `BotDetector.UserAgent` | on workers; first match wins | recognize bots by user agent, IP, header-derived dimensions |
| `IUserAgentParser` | `Web.UserAgentParser` | `UserAgentParser.Default` | on workers | browser, OS and device |
| `IGeoLocator` | `Web.GeoLocator` | none | on workers, once per session | country, region and city |
| `IEventEnricher` | `Enrichers` | `EventEnricher.NormalizePage(...)` | on workers, last, in order | add, change or remove dimensions |

```csharp
// Exclude office traffic.
options.EventFilters.Add(EventFilter.Create(e => e.IpAddress?.StartsWith("10.") != true));

// Treat your own load tests as bots, before the built-in detector.
options.Web.BotDetectors.Insert(0, BotDetector.Create(context =>
    context.TrackedDimensions.ContainsKey("load_test") ? new BotInfo("Load test", BotCategories.Automation) : null));

// Use a third-party parser.
options.Web.UserAgentParser = UserAgentParser.Create(ua => MyParser.Parse(ua) is { } r
    ? new UserAgentInfo(r.Browser, r.BrowserMajor, r.Os, r.OsVersion, r.DeviceType)
    : null);

// Collapse ids and derive a content section.
options.Enrichers.Add(EventEnricher.NormalizePage(path => Regex.Replace(path, @"/\d+", "/:id")));
options.Enrichers.Add(EventEnricher.Create(context =>
    context.SetDimension("section", context.GetDimension(StandardDimensions.Page)?.Split('/')[1])));
```

Rules that hold for all of them:

* **Thread safety.** Extensions are called concurrently; implementations must be thread-safe.
* **Fail open.** An exception thrown by an extension is logged and counted in `GetStatistics().Errors`, and the event is processed as if the extension were not there. Analytics never breaks the host application.
* **Privacy.** Extensions see the IP address and user agent in memory (`EventContext`); neither is stored.
* **Enrichers see final values.** `GetDimension` and `SetDimension` work on page, hostname, custom and session-level dimensions after built-in enrichment, for human and bot events (`context.Bot`). A session-level value set on the first event of a session applies to the whole session. The entry page follows the page and cannot be set directly, and the bot dimensions of bot events are fixed.

---

## How it uses ZoneTree

ZoneTree is an ordered, persistent LSM key-value engine. ZoneTree.Analytics is a data model on top of it:

```text
data/analytics/
  meta/                       configuration, secrets, daily salts (synchronous WAL)
  sessions/                   active sessions keyed by visitor hash (TTL deletion)
  events/2026-10/             raw events, one ZoneTree per month by default
  rollups/day/2026/           daily rollups, one ZoneTree per year by default
  rollups/hour/2026-10/       hourly rollups, one ZoneTree per month by default
  rollups/minute/2026-10-08/  minute rollups, one ZoneTree per day by default
```

* **Order-preserving keys.** Rollup keys are `site · event · dimension-set · bucket · values`, so every rollup sub-query is one contiguous range scan. Raw event keys are `site · timestamp · sequence`.
* **Pre-aggregation with atomic merges.** Workers aggregate in memory and merge rows with `TryAtomicAddOrUpdate`. Stored values are immutable; every merge creates a new value.
* **Time partitioning.** Each period is its own ZoneTree with its own maintainer; the period of every tier is configurable, see [Partitioning](#partitioning). Idle partitions are merged to disk and closed; retention drops whole partitions with `Maintenance.Drop()`.
* **Sessions with TTL.** Expired sessions disappear through ZoneTree's deletion predicate and are removed by normal merges.
* **Bounded memory.** Memory is governed by ZoneTree's mutable segment limits per open partition, not by the amount of data.
* **Durability.** Data stores use ZoneTree's asynchronous compressed write-ahead log.

---

## Performance

`src/ZoneTree.Analytics.Playground` generates one million page views over 30 days (50,000 recurring visitors, 12 pages, 12 referrers, 7 user agents including a bot) and measures ingestion and queries:

```bash
dotnet run -c Release --project src/ZoneTree.Analytics.Playground -- 1000000 30 1
```

Almost every event starts a new session, a worst case for ingestion. On an Intel Core Ultra 7 265KF with a desktop hard disk:

| Measure | Result |
| --- | ---: |
| Ingestion, 1 worker | ~80,000 events/s |
| Ingestion, 4 workers | ~145,000 events/s |
| 30-day summary, with or without comparison | < 1 ms |
| 30-day daily time series, 7-day hourly time series | 1–1.5 ms |
| 30-day pages, sources, countries | 2–6 ms |
| Unaligned range (rollups plus raw edges) | ~10 ms |
| Ad-hoc two-filter breakdown, 1 day of raw events | ~30 ms |
| Ad-hoc two-filter breakdown, 30 days of raw events (~860k events) | ~0.8 s |
| Segment over 7 days | ~150 ms |
| Funnel over 7 days | ~90 ms |
| Disk usage | ~120–150 bytes per event, rollups included |

Rollup query time depends on the number of rows in the range, not on the number of events.

---

## Operations

* **Freshness.** Rollup-based answers lag ingestion by at most `Ingestion.FlushInterval` (2 seconds). `await analytics.FlushAsync()` makes everything tracked so far visible. Raw events, and therefore `GetCurrentVisitors`, are real-time.
* **Durability.** After a crash, aggregates buffered since the last flush are lost, and recent records follow ZoneTree's write-ahead log guarantees. A graceful `Dispose` processes the queue and flushes everything.
* **Statistics.** `analytics.GetStatistics()` reports accepted, dropped, processed, rejected, filtered and bot events, sessions, flushes, errors, queue length and open partitions.
* **Logging.** Pass `Storage.Logger` (a ZoneTree `ILogger`); the ASP.NET Core package bridges to `Microsoft.Extensions.Logging`.
* **Backups.** Every partition is a regular ZoneTree directory. For a consistent offline backup, dispose the engine and copy the data directory.
* **Retention** runs hourly by default (`Retention.CheckInterval`) and can be triggered with `ApplyRetention()`.

---

## License

MIT. See [LICENSE](LICENSE).
