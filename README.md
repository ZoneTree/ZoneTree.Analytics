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

var report = await analytics.Web.GetSummaryAsync(new WebReportRequest { Range = DateRange.LastDays(30) });
// report.Summary: Visitors, PageViews, Sessions, BounceRate, AverageSessionDuration, PageViewsPerSession
```

---

## Features

* **Web analytics out of the box.** Visitors, page views, sessions, bounce rate, session duration, entry and exit pages, sources, channels, referrers, UTM campaigns, geography, browsers, operating systems and devices.
* **Generic events and metrics.** Any event name with custom dimensions and numeric values: product analytics, telemetry and business KPIs.
* **One query model.** Metrics such as visitors, bounce rate, revenue and conversion rate in one query, grouped and filtered, as totals or time series from minute to year, compared with a previous period.
* **Time series and metrics.** Percentiles such as p95 latency, histograms, standard deviation, first and last values of gauges, distinct counts of any dimension, conditional metrics (CountIf, SumIf), metric formulas, rates, running totals, moving averages, lag and lead, counter increases, 5-minute or 6-hour buckets, gap filling, conditions on results (HAVING) and the top rows of every bucket.
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
    Values = { ["revenue"] = 49.0 },
});

// Telemetry without a visitor or page.
analytics.Track(new AnalyticsEvent("job_completed")
    .WithDimension("queue", "emails")
    .WithValue("duration_ms", 812));
```

`Track` validates the event, copies it, queues it and returns immediately, so the instance can be reused. Enrichment, session tracking, storage and aggregation happen on background workers. Event names starting with `$` are reserved.

### Read reports

```csharp
var request = new WebReportRequest
{
    Range = DateRange.LastDays(30),
    Compare = ComparisonPeriod.PreviousPeriod,
};

WebSummary summary = (await analytics.Web.GetSummaryAsync(request)).Summary;
var daily     = await analytics.Web.GetTimeseriesAsync(request, TimeInterval.Day);  // daily.Rows
var pages     = await analytics.Web.GetPagesAsync(request);       // views, visitors, entrances, exits
var exitPages = await analytics.Web.GetExitPagesAsync(request);
var sources   = await analytics.Web.GetBreakdownAsync(request, StandardDimensions.Source);
var countries = await analytics.Web.GetBreakdownAsync(request, StandardDimensions.Country);
var goals     = await analytics.Web.GetGoalsAsync(request);       // custom events and conversion rates
long? live    = analytics.Web.GetCurrentVisitors();               // last 5 minutes, real-time

Console.WriteLine($"{summary.Visitors} visitors (previously {summary.Previous.Visitors})");
```

Every report carries the range it covered (`From`, `To`) and its `Plan`, whose warnings tell when a result is incomplete, for example because it reaches before raw event retention. List reports and time series are `WebReport<T>` with `Rows` and `TotalRows`; the summary is a `WebSummaryReport` with `Summary`.

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

### API conventions

* **Synchronous and asynchronous.** Every operation that reads or writes storage has both forms with the same parameters: `Query`/`QueryAsync` (for `AnalyticsQuery` and `FunnelQuery`), `Flush`, `Compact`, `GetEventNames`, `EraseVisitor`, `ApplyRetention` and every `Web` report. Asynchronous forms run the work on a thread-pool thread; use them in request handlers, and the synchronous ones in background jobs and scripts. `Track` queues the event and returns at once; `TrackAsync` also waits asynchronously when the queue is full.
* **Cancellation.** Every operation that can take time takes a `CancellationToken` as its last, optional parameter. `Queries.Timeout` applies on top of it.
* **Errors.** Every exception the engine raises derives from `AnalyticsException`: `AnalyticsConfigurationException` for options that conflict with the data directory, `AnalyticsQueryException` for queries that cannot be answered, and its subclass `AnalyticsQueryTimeoutException`. Invalid arguments raise `ArgumentException`.
* **Options.** Option groups and collections are never null and are set in place: `new AnalyticsOptions { Retention = { RawEvents = TimeSpan.FromDays(30) } }`, `options.Rollups.Definitions.Add(...)`. Allow-lists such as `Privacy.AllowedDimensions` are null when there is no restriction.
* **Testing.** `AnalyticsEngine` implements `IAnalyticsTracker` (`Track` and `TrackAsync`). Code that only tracks events can depend on the interface and use a fake in tests. Test queries against a real engine on a temporary directory: it is embedded and opens in milliseconds.

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

A query has a base `Event` (page views by default), a `Range`, an optional `Interval` and `Fill`, `GroupBy` dimensions, `Filters`, `Segments`, `Metrics`, `Having` conditions, `OrderBy`, `Limit`, `Offset` and an optional `Compare`.

### Metrics

| Metric | Reads | Meaning |
| --- | --- | --- |
| `Events`, `Visitors` | base event | count and unique visitors |
| `EventsOf(e)`, `VisitorsOf(e)` | event `e` | count and unique visitors of another event |
| `PageViews` | page views | page view count |
| `Sessions`, `Bounces`, `BounceRate`, `SessionDuration`, `AverageSessionDuration`, `PageViewsPerSession` | sessions | session metrics; rates are 0 to 1, durations are seconds |
| `Entrances`, `Exits`, `ExitRate` | page views | sessions that started or ended on a page |
| `Sum(v)`, `Average(v)`, `Min(v)`, `Max(v)`, `Count(v)` | base event, or `Sum(v, e)` | aggregates of a tracked value |
| `StandardDeviation(v)`, `Variance(v)` | base event, or with `e` | population spread of a tracked value |
| `Percentile(v, p)`, `Median(v)` | base event, or with `e` | percentiles within 1%; 0 and 100 are the exact minimum and maximum ¹ |
| `First(v)`, `Last(v)` | base event, or with `e` | the first and last value by time: gauges such as a temperature ¹ |
| `Distinct(d)` | base event, or with `e` | distinct values of a dimension, such as tenants or countries ¹ |
| `CountBetween(v, lower, upper)`, `Histogram(v, edges)` | base event, or with `e` | values in `[lower, upper)`, placed within 1%; a histogram is one per bin ¹ |
| `m.Where(filters)` | as `m` | `m` over the events that match the filters: CountIf, SumIf and so on |
| `VisitorsOf(goal)`, `ConversionRate(goal)` | goal event | goal visitors, and goal visitors ÷ base visitors |
| `PercentOfTotal(m)` | as `m` | each row's share of the total |
| `BotHits` | bot events | bot requests, see [Bot traffic](#bot-traffic) |

¹ Answered from rollups when configured (see [Where answers come from](#where-answers-come-from)), otherwise from raw events.

The engine runs one sub-query per event the metrics read, and per set of `Where` conditions, and joins them on the time bucket and group values. A row is returned for every group that has data for any metric; set `IncludeRowsWithoutBaseEvent = false` to keep only groups with base events. Filters apply to every sub-query.

```csharp
var errors = Metric.Events.Where(Filter.GreaterThanOrEqual("status", 500));
query.Metrics.AddRange([Metric.Events, errors, errors / Metric.Events]);   // error rate in one query
query.Metrics.AddRange(Metric.Histogram("load_time", [0, 100, 250, 500, 1000, double.PositiveInfinity]));
```

A conditional metric is answered from rollups when a rollup definition covers the group-by and filter dimensions together with the dimensions of its conditions, otherwise from raw events; on a formula, the conditions apply to every aggregate in it. Histograms and `CountBetween` read the percentile sketches, so they need `ValueAggregates.Percentiles` to be answered from rollups.

Session metrics can only be grouped and filtered by session-level dimensions (standard and `Sessions.Dimensions`) and the entry page; entrances and exits also by page and hostname. Use a segment to select sessions by anything else. Bot metrics can only be grouped and filtered by page, hostname, `event`, `bot` and `bot_category`, and cannot be combined with segments.

Every metric has a stable key, for example `visitors`, `bounce_rate`, `sum:revenue`, `avg:duration_ms@job_completed`, `p95:load_time`, `distinct:tenant`, `conversion_rate:signup`, `percent(visitors)`, `div(sum:revenue,events:purchase)`, `moving_avg(visitors,7)`, `count_between(load_time,100,500)` or `where(events,"status>=500")` (filters in double quotes, with backslash escapes); `Metric.Parse` turns keys back into metrics.

### Formulas and time series

```csharp
var averageOrder = Metric.Sum("revenue") / Metric.Events;

var result = await analytics.QueryAsync(new AnalyticsQuery
{
    Event = "purchase",
    Range = DateRange.ThisMonth,
    Interval = TimeInterval.Day,                              // or TimeInterval.Minutes(5), TimeInterval.Hours(6)
    Metrics =
    {
        averageOrder,
        Metric.Cumulative(Metric.Sum("revenue")),             // month to date
        Metric.MovingAverage(Metric.Visitors, 7),             // 7-day moving average
        Metric.Rate(Metric.Events, TimeSpan.FromHours(1)),    // purchases per hour
    },
});
```

Metrics combine with `+`, `-`, `*` and `/`, with each other and with numbers; division by zero, and a result that overflows, gives zero. The totals row applies a formula to the totals, so the average order value of the range is total revenue ÷ total orders.

| Series function | Rows | Totals row |
| --- | --- | --- |
| `Rate(m, per)` | `m` per second, or per `per`, over the bucket's part of the range up to now | average rate over the range |
| `Cumulative(m)` | running total of `m` | total of `m` |
| `MovingAverage(m, n)` | average of `m` over the current and `n - 1` previous buckets | total of `m` |
| `Difference(m)` | change of `m` from the previous bucket | sum of the changes |
| `Increase(m)` | increase of a counter such as `Last("energy_kwh")`; a decrease is a reset | sum of the increases |
| `Lag(m, n)`, `Lead(m, n)` | `m` of the bucket `n` earlier or later, zero where there is none: `Metric.Visitors / Metric.Lag(Metric.Visitors, 7) - 1` is week-over-week growth | total of `m` |

Series functions run along each group's buckets in time order and need an `Interval`, except `Rate`. The first bucket of a group has no change.

**Intervals.** `Minute`, `Hour`, `Day`, `Week`, `Month` and `Year`, or fixed widths `TimeInterval.Minutes(n)` and `TimeInterval.Hours(n)` that divide a day (5, 15 or 30 minutes; 2, 3, 4, 6, 8 or 12 hours), aligned to UTC midnight. Minute and hour buckets are UTC; day and longer buckets follow the query's time zone. Widths are answered from the finest rollups that fit: minute rollups for minutes, hourly rollups for hours.

**Fill.** `Fill` decides what a time series does with buckets without data, for every group with data in the range: `FillMode.Zero` (the default) adds them with zero values, `None` leaves them out, `Previous` repeats the previous values with data (for gauges), and `Linear` interpolates between the buckets with data around them. Filling applies to aggregates; formulas and series functions are computed from the filled values. A fill that would create more than `Queries.MaxFilledRows` rows (1,000,000 by default) is rejected.

**Having.** `Having` keeps rows whose metric values satisfy every condition, like SQL `HAVING`; `TotalRows` counts the rows that remain, totals are not affected. `MetricFilter.Parse("pageviews>100")` accepts `==`, `!=`, `>`, `>=`, `<` and `<=`.

**Top rows per bucket.** `LimitPerBucket` keeps the first rows of each bucket after `OrderBy`, for example the slowest page of every hour; it also answers ArgMax questions, because the page of the slowest load is the page with the largest maximum:

```csharp
GroupBy = { StandardDimensions.Page },
Metrics = { Metric.Max("load_time") },
OrderBy = { Order.ByBucket(), Order.By(Metric.Max("load_time")) },
LimitPerBucket = 1,
```

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
DateRange.ThisMonth; DateRange.LastMonth; DateRange.ThisYear; DateRange.LastYear; DateRange.Month(2026, 9); DateRange.Year(2026);
DateRange.Days(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 14));
DateRange.Last(TimeSpan.FromMinutes(30));
DateRange.Between(from, to);

query.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
query.Compare = ComparisonPeriod.PreviousPeriod;   // or PreviousYear, or Custom(range)
```

**Text forms.** Ranges, comparison periods, filters, metrics, conditions, sort keys, segments and intervals each have a text form: `Parse` reads it, `TryParse` tries to, and `ToString()` writes it back. The reporting API uses the same syntax.

| Type | Examples |
| --- | --- |
| `DateRange` | `today`, `yesterday`, `30d`, `today-7..today-1`, `12h`, `30m`, `this-month`, `last-month`, `this-year`, `last-year`, `2026`, `2026-02`, `2026-02-28`, `2026-02-01..2026-02-28`, two ISO 8601 date-times separated by `..` |
| `ComparisonPeriod` | `previous-period`, `previous-year`, or a date range |
| `Filter` | `country==DE\|AT`, `page^=/blog/`, `load_time>=500`, `utm_source?` |
| `Metric` | `visitors`, `sum:revenue`, `p95:load_time`, `percent(visitors)` |
| `MetricFilter` | `visitors>10` |
| `Order` | `-visitors`, `page` (a group-by dimension), `bucket` |
| `Segment` | `visitors with purchase where plan==pro; country==DE`, `not sessions with signup` |
| `TimeInterval` | `day`, `hour`, `5m`, `6h` |

Invalid text raises `FormatException`; a null text raises `ArgumentNullException`.

Calendar ranges and day, week, month and year buckets follow the query's time zone (the engine's reporting time zone by default). Daily rollups are stored in the reporting time zone; other time zones are answered from hourly rollups. A time series has at most `Queries.MaxTimeBuckets` buckets (100,000 by default); longer ones are rejected rather than truncated. A query groups by at most `Queries.MaxGroupBy` dimensions (8 by default).

Queries and funnels that run longer than `Queries.Timeout` (30 seconds by default) are cancelled with an `AnalyticsQueryTimeoutException`, a kind of `AnalyticsQueryException`, so an expensive ad-hoc query cannot tie up the host application.

### Filters

```csharp
Filter.Equal("country", "DE", "AT");        Filter.NotEqual("page", "/");
Filter.Contains("page", "docs");            Filter.NotContains("page", "admin");
Filter.StartsWith("page", "/blog/");        Filter.EndsWith("page", ".html");
Filter.Matches("page", @"^/blog/\d+$");     Filter.NotMatches("referrer", "spam");
Filter.IsSet("plan");                       Filter.IsNotSet("plan");
Filter.GreaterThanOrEqual("status", 500);   Filter.LessThan("duration", 1.5);
new Filter("browser", FilterOperator.Equal, ignoreCase: true, "chrome");
Filter.Parse("country==DE|AT");             // also !=, ~=, !~=, ^=, $=, =~, !=~, >, >=, <, <=, dim?, !dim?
```

Positive operators never match a missing dimension; negative operators always do. Numeric operators compare values as numbers; values that are not numbers do not match.

### Segments

```csharp
query.Segments.Add(Segment.VisitorsWho("purchase"));
query.Segments.Add(Segment.SessionsWith(StandardEvents.PageView, Filter.Equal("page", "/pricing")));
query.Segments.Add(Segment.VisitorsWhoViewed("/pricing").Exclude());
query.Segments.Add(Segment.Parse("visitors with purchase where plan==pro; country==DE"));
```

A segment restricts a query to visitors (or sessions) with at least one matching event in the range, or without one when excluded. Segments are evaluated over raw events.

### Funnels

```csharp
var funnel = await analytics.QueryAsync(new FunnelQuery
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

A funnel has two to 64 steps.

### Where answers come from

Ingestion maintains **rollups**: pre-aggregated rows per time bucket for configured dimension sets. A sub-query is answered from rollups when the union of its group-by and filter dimensions equals a rollup's dimension set. The range is covered greedily: whole days from daily rollups, remaining hours from hourly rollups, remaining minutes from minute rollups, and any remainder from raw events. Other dimension combinations, segments and funnels scan **raw events** within raw event retention. `result.Plan` shows the source of each part; `Source = QuerySource.Rollups` or `QuerySource.RawEvents` forces one, and both give the same numbers.

Raw scans run in parallel. A range is split into time slices, within partitions, that are scanned on up to `Queries.MaxDegreeOfParallelism` threads (the processor count by default) and merged; the results equal those of a sequential scan, also for sessions and funnels that span slices. Lower it when many raw queries run at the same time and should share the processors; `1` scans on the calling thread only.

Add rollups for hot combinations:

```csharp
options.Rollups.Definitions.Add(new RollupDefinition(StandardDimensions.Country, StandardDimensions.Page));
options.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] });
```

A rollup added to an existing data directory, re-added after being removed, or given different `Events` covers data from that moment on; earlier ranges are answered from raw events.

Every tracked value has its sum, count, minimum, maximum, average and variance in rollups. Percentiles, first and last values, and distinct counts of dimensions cost storage in every rollup row they reach, so they are opt-in:

```csharp
options.Rollups.ValueAggregates["load_time"] = ValueAggregates.Percentiles;
options.Rollups.ValueAggregates["temperature"] = ValueAggregates.FirstAndLast;
options.Rollups.DistinctDimensions.Add("tenant");
```

Without them, and for ranges before they were configured, these metrics are answered from raw events; with raw events disabled such a query fails with a message that names the missing setting. Percentiles use a mergeable quantile sketch (DDSketch) with a relative error of at most 1%, so rollups and raw events give identical percentiles.

---

## Web analytics model

### Standard dimensions

| Dimension | Source |
| --- | --- |
| `page`, `hostname` | Page URL. Query strings are removed by default; UTM parameters are extracted. |
| `entry_page` | First page of the session. |
| `source` | `utm_source`, a known referrer name ("Google", "Twitter", "Hacker News", "ChatGPT", ...), the referrer host, or "Direct". |
| `referrer` | Referrer host, empty for direct traffic. |
| `channel` | Direct, Organic Search, Paid Search, Organic Social, Paid Social, Email, Affiliates, AI Assistants, Video, Referral or Paid Other (constants in `Channels`). |
| `utm_source`, `utm_medium`, `utm_campaign`, `utm_term`, `utm_content` | Campaign parameters of the landing URL. |
| `country`, `region`, `city` | Your `IGeoLocator`, or values you pass as dimensions (for example from a CDN header). |
| `browser`, `os`, `device` (+ `browser_version`, `os_version`) | Built-in, dependency-free user agent parser, or your `IUserAgentParser`. |
| `bot`, `bot_category`, `event` | On bot events only: the bot, its category and the original event name. |

Source, campaign, geography and device are **session-level**: captured from the first event of a session and attached to every event of the session, so all page views and goals of a visit share them. Standard dimensions can be overridden per event, for example `Dimensions = { [StandardDimensions.Country] = cdnCountry }`.

### Sessions

A session is a sequence of events from one visitor with no gap longer than `Sessions.Timeout` (30 minutes by default). A session bounces when it has at most one interactive event; page views and custom events are interactive unless `NonInteractive` is set. Session metrics are attributed to the session start; entrances and exits to their page views. Sessions are maintained incrementally, so session metrics are real-time.

Custom dimensions can be session-level too, for example an experiment variant or a plan:

```csharp
options.Sessions.Dimensions.Add("ab_variant");
options.Rollups.Definitions.Add(new RollupDefinition("ab_variant"));

analytics.Track(AnalyticsEvent.PageView(url, referrer, ip, userAgent).WithDimension("ab_variant", "B"));

var variants = await analytics.Web.GetBreakdownAsync(request, "ab_variant");   // with sessions and bounce rate
```

Like source or country, the value is taken from the first event of a session and applies to the whole session; values on later events are ignored. Session metrics, entrances and exits can then be grouped and filtered by it, and enrichers can set it. Custom session dimensions can be added or removed at any time.

### Bot traffic

Every event goes through the bot detectors (`Web.BotDetectors`). The built-in detector recognizes bots by user agent and names them, for example `Googlebot`, `GPTBot`, `ClaudeBot`, `AhrefsBot`, `facebookexternalhit`, `UptimeRobot`, `HeadlessChrome` or `curl`, in the categories of `BotCategories`: Search, AI, SEO, Social Preview, Monitoring, Automation, Scripts, Archiver and Other.

`Web.Bots` decides what happens to bot traffic:

* `BotTraffic.Exclude` (default): bot events are discarded and counted in `BotsExcluded`.
* `BotTraffic.TrackSeparately`: bot events are stored as `$bot` events with the dimensions `bot`, `bot_category`, `event` (the original event name), `page` and `hostname`. They never count as visitors, page views, sessions or goals, and they have no visitor identity.

With `TrackSeparately`, the web reports show bot hits next to human traffic:

```csharp
options.Web.Bots = BotTraffic.TrackSeparately;

var summary = (await analytics.Web.GetSummaryAsync(request)).Summary;   // BotHits next to PageViews
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

Use one of the two page view options, not both. The tracker follows single-page-app navigation, uses `sendBeacon`, sets no cookies and ignores localhost unless `data-allow-localhost="true"` is set. The engine is a DI singleton, also registered as `IAnalyticsTracker`, that is flushed and disposed on shutdown, and its logs go to `Microsoft.Extensions.Logging`.

### Reporting API

| Endpoint | Returns |
| --- | --- |
| `GET summary` | headline metrics: `{ from, to, summary, plan }` |
| `GET timeseries?interval=day` | headline metrics per bucket: `{ from, to, totalRows, rows, plan }` |
| `GET breakdown?dimension=source` | rows with visitors, page views, share and session metrics |
| `GET pages`, `GET exit-pages` | page statistics |
| `GET goals` | custom events with conversion rates |
| `GET bots` | bots with category, hits and share (with `BotTraffic.TrackSeparately`) |
| `GET realtime?minutes=5` | current visitors |
| `POST query` | any query, as JSON |
| `POST funnel` | a funnel, as JSON |

List reports return `{ from, to, totalRows, rows, plan }`. Summary, time series and page rows include `botHits` (null unless bots are tracked separately). `interval` accepts `minute`, `hour`, `day`, `week`, `month`, `year` and widths such as `5m` or `6h`; `POST query` also takes `fill` (`none`, `zero`, `previous`, `linear`), `having` conditions, `orderBy` sort keys and `limitPerBucket`. Common parameters, all in the text forms above: `site`, `range` (default `30d`) or `from` and `to` (days or date-times), `tz`, `filter` (repeatable), `segment` (repeatable; in JSON, `segments` holds texts or objects), `compare` (`previous-period`, `previous-year` or a range), `limit`, `offset`.

```json
POST /_analytics/api/query
{
  "range": "30d",
  "groupBy": ["source"],
  "metrics": ["visitors", "bounce_rate", "conversion_rate:signup"],
  "filters": ["country==DE|AT"],
  "having": ["visitors>10"],
  "segments": ["not visitors with signup"],
  "orderBy": ["-visitors"],
  "limit": 10,
  "compare": "previous-period"
}
```

Invalid requests return 400 with an `error` message; queries that exceed `Queries.Timeout` return 503. The API exposes analytics data: protect it with authorization, and restrict sites with `AuthorizeSite`.

---

## Privacy

* **No IP addresses or user agents are stored.** They are used in memory for visitor hashing, geolocation and device detection, then discarded.
* **Visitor identification** (`Privacy.VisitorIdentification`):
  * `DailyRotatingHash` (default): anonymous visitors are identified by HMAC-SHA256 of site, IP and user agent with a random salt that rotates every UTC day and is then destroyed, so visitors cannot be followed across days and hashes cannot be recomputed. Explicit `VisitorId` values are hashed with a persistent secret.
  * `PersistentHash`: anonymous visitors are recognized across days. Less private.
  * `ExplicitOnly`: only explicit `VisitorId` values identify visitors.
  * `None`: no visitor tracking, no sessions or unique counts.
* **Secret management.** Supply `Privacy.VisitorIdSecret` from your secret store to keep the hashing key away from the data files.
* **Data minimization.** `GeoPrecision`, `CollectUserAgentDetails`, `CollectVersions`, `IncludeQueryString` (off by default), `AllowedDimensions`, `DeniedDimensions`, `MaxCustomDimensions`, `MaxCustomValues`, `MaxValueLength`.
* **Filtering.** `EventFilters` drop events before they are queued, for example internal traffic or visitors who opted out. Bot traffic is excluded by default.
* **Retention** per data tier, see [Retention](#retention). Expired data is hidden at once and removed from disk; late events older than a tier's retention are not written to that tier, so backfilled history only lands where it is kept.
* **Erasure.** `analytics.EraseVisitor(userId)` or `await analytics.EraseVisitorAsync(userId)` deletes the raw events and the session of an explicitly identified visitor. Rollups contain only aggregates and sketches, never identifiers.

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
    Sessions = { Timeout = TimeSpan.FromMinutes(30), Dimensions = { "ab_variant" } },
    Queries = { Timeout = TimeSpan.FromSeconds(10), MaxDegreeOfParallelism = 4 },
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
        Partitioning = { RawEvents = 50_000_000 },  // 50 million raw events per ZoneTree
        Rollups = { ConfigureMaintainer = maintainer => maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromMinutes(1) },
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

The engine copies the options when it is created; changing them afterwards has no effect. The reporting time zone and sketch precision define the storage format. They are persisted on creation; reopening with different values throws `AnalyticsConfigurationException`.

### Retention

`Retention` keeps raw events for 90 days, minute rollups for 2 days, hourly rollups for 400 days and daily rollups forever by default. Retention works inside every tree, whatever its size:

* **Hidden at once.** The stores' ZoneTree deletion predicates compare the time in each key with the tier's cutoff, so expired records disappear for every reader as soon as the cutoff passes them. Cutoffs move in whole UTC days (whole hours for retention under a day): data is kept for its retention and at most one more day.
* **Removed from disk.** Retention runs, hourly by default (`Retention.CheckInterval`) or through `ApplyRetention()`, drop the partitions that expired as a whole. In the other partitions they delete the first record of every expired key range (a site's raw events; a rollup series), which makes the next merge read the disk segment parts that hold expired records and rewrite them without those records. A part that lies within one key range and expired as a whole, such as the old events of a busy site, is dropped without being read (ZoneTree's range deletion). A tier does this once per cutoff step, so the oldest part of each range is rewritten at most once a day.
* **Large trees.** A tree larger than `Storage.<store>.DiskSegmentMaxItemCount` (20 million records by default) keeps older data in bottom segments; expired records there leave the disk when the bottom segments merge, which the engine starts whenever two or more exist.
* **Late events.** Events older than a tier's retention are not written to that tier.

### Partitioning

Each data tier is a sequence of partitions, and each partition is a separate ZoneTree that holds a contiguous time range. A tier writes to its latest partition until that partition holds the number of records set in `Storage.Partitioning`, then starts a new one:

| Tier | Option | Default |
| --- | --- | ---: |
| Raw events | `RawEvents` | 10,000,000 |
| Minute rollups | `MinuteRollups` | 10,000,000 |
| Hourly rollups | `HourRollups` | 10,000,000 |
| Daily rollups | `DayRollups` | 10,000,000 |

A ZoneTree comfortably holds tens of millions of records (ZoneTree's reference benchmark keeps 100 million in one tree), and one large tree ingests and answers long-range queries faster than several small ones. A tier therefore never starts a tree for time alone: a site that stores a million events a year keeps one raw event tree for a decade. The limit can be changed at any time and applies to the next partition; raise it for very high-volume sites.

* **Rollover.** Housekeeping checks the latest partition of each tier every minute. Once the latest partition holds the limit, the next partition starts at a later UTC day boundary, beyond any event the engine can still receive (`Ingestion.MaxFutureSkew`, one hour by default, plus a day). Until then the latest partition keeps receiving events, so partitions slightly exceed the limit. Partition directories are named after their start day.
* **Ranges.** The first partition of a tier also holds events older than its start, and the latest one every newer event. Queries read only the partitions that overlap their range.
* **Backfills.** Events with an explicit `Timestamp` go to the partition that holds their time, so a backfill adds to existing partitions instead of creating new ones.
* **Counts.** Records are counted approximately: deleted records that merges have not removed yet are included. `GetStatistics().Partitions` lists every partition with its approximate record count.

Retention does not depend on partitions: expired records are deleted inside every tree (see [Retention](#retention)), and a partition is dropped once its whole range is older than its tier's retention.

#### How many ZoneTrees an engine creates

The number of ZoneTrees depends on the number of records each tier retains, not on the number of sites or rollup definitions: all sites and all rollup definitions of a tier share its trees.

```
trees = 1 (sessions, when enabled)
      + for raw events and each enabled rollup granularity:
          about (records within retention / records per partition) + 1
```

With the defaults (10 million records per partition; raw events for 90 days, hourly rollups for 400 days, daily rollups forever; minute rollups disabled):

| Scenario | Raw | Minute | Hourly | Daily | Sessions | Trees on disk |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 million events over 30 days (the playground) | 1 | 0 | 1 | 1 | 1 | **4** |
| The playground's traffic for years: 10 million events a year (2.5 million within retention), 1.4 million hourly rows within retention, 56,000 daily rows a year | 1 | 0 | 1 | 1 | 1 | **4** |
| 100 million events a year: 25 million raw events within retention | 3–4 | 0 | 1–2 | 1 | 1 | **6–8** |
| Defaults plus minute rollups (2-day retention) | | 1 | | | | **+1** |

Only recently used trees are open: a partition that has not been written or read for `Storage.PartitionIdleTimeout` (10 minutes) is merged to disk and closed. Live traffic writes to the latest partition of each tier, so an ingesting engine with the defaults holds 4 trees open, plus the partitions that recent queries read.

### Storage tuning

`Storage.RawEvents`, `Storage.Rollups` and `Storage.Sessions` tune the ZoneTrees of each kind of store, every partition included. Settings left null keep ZoneTree's defaults:

| Setting | Default |
| --- | --- |
| `WriteAheadLogMode` | `AsyncCompressed` |
| `MutableSegmentMaxItemCount` | raw events 50,000, rollups 100,000, sessions 50,000 |
| `MutableSegmentBloomFilterBitsPerItem` | raw events 0 (scanned, never point-read); ZoneTree's default otherwise |
| `DiskSegmentMaxItemCount`, `BTreeLockMode`, `BTreeNodeSize`, `BTreeLeafSize`, `EnableSingleSegmentGarbageCollection`, `AllowUnsafeOptionValues` | ZoneTree's defaults |
| `ConfigureWriteAheadLog`, `ConfigureDiskSegments`, `ConfigureMaintainer` | hooks for every other write-ahead-log, disk segment and maintainer option |

Rollup keys are ordered by series, then time, so the rows of a new hour spread over the whole key range of a rollup tree and merges rewrite most of its disk segment. A larger rollup mutable segment merges less often and absorbs repeated updates of the same row; a smaller one holds less memory, as rows carry visitor sketches. With one hourly rollup tree, raising it from 20,000 to 100,000 rows lowered ingestion CPU time by about 7%, and to 200,000 by about 11%.

```csharp
options.Storage.RawEvents.ConfigureDiskSegments = disk => disk.CompressionMethod = CompressionMethod.Zstd;
options.Storage.Rollups.ConfigureMaintainer = maintainer => maintainer.ThresholdForMergeOperationStart = 4;
options.Storage.WriteAheadLogDirectory = "/fast-disk/analytics-wal";
```

`Storage.WriteAheadLogDirectory` moves write-ahead logs to their own root, for example a faster disk; it is fixed once the data directory exists. Comparers, serializers and deletion predicates define the storage format and are chosen by the engine. ZoneTree validates these settings when the engine starts, before the first event.

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
  engine.lock                 held by the engine that has the directory open
  meta/                       configuration, secrets, daily salts, rollup activations (two checksummed files)
  sessions/                   active sessions keyed by visitor hash (TTL deletion)
  events/2026-10-11/          raw events, 10 million per ZoneTree by default
  rollups/day/2026-10-11/     daily rollups, likewise
  rollups/hour/2026-10-11/    hourly rollups, likewise
  rollups/minute/2026-10-11/  minute rollups, likewise
```

* **Order-preserving keys.** Rollup keys are `site · event · dimension-set · bucket · values`, so every rollup sub-query is one contiguous range scan. Raw event keys are `site · timestamp · sequence`.
* **Pre-aggregation with atomic merges.** Workers aggregate in memory and merge rows with `TryAtomicAddOrUpdate`. Stored values are immutable; every merge creates a new value.
* **Partitioning by size.** Each tier is a sequence of ZoneTrees, each with its own maintainer and a contiguous time range; a tier starts a new tree when the latest one holds 10 million records by default, see [Partitioning](#partitioning). Idle partitions are merged to disk and closed; partitions that expire as a whole are dropped with `Maintenance.Drop()`.
* **Retention by deletion predicate.** Raw event and rollup trees treat records older than retention as deleted, so readers skip them and merges drop them. Deleting the first record of each expired key range makes merges read parts they would otherwise copy unchanged, so expired data leaves the disk inside large, long-lived trees too; parts within one key range that expired as a whole are dropped without being read (`IsRangeDeleted`). Maintainers merge bottom segments whenever two or more exist.
* **Sessions with TTL.** Expired sessions disappear through ZoneTree's deletion predicate and are removed by normal merges.
* **Bounded memory.** Memory is governed by ZoneTree's mutable segment limits per open partition, not by the amount of data.
* **Durability.** Data stores use ZoneTree's asynchronous compressed write-ahead log by default. The few kilobytes of engine metadata live in two files written alternately, each flushed to disk and checksummed, so a crash during a write never loses the previous state.
* **One engine per directory.** The engine holds `engine.lock` exclusively while the directory is open; a second engine, in this or another process, fails with an `IOException` instead of corrupting data.

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
* **Statistics.** `analytics.GetStatistics()` reports accepted, dropped, processed, rejected, filtered and bot events, sessions, flushes, errors, queue length, open partitions, and every partition with its approximate record count.
* **Logging.** Pass `Storage.Logger` (a ZoneTree `ILogger`); the ASP.NET Core package bridges to `Microsoft.Extensions.Logging`.
* **Backups.** Every partition is a regular ZoneTree directory. For a consistent offline backup, dispose the engine and copy the data directory, and `Storage.WriteAheadLogDirectory` when it is set: the logs hold records that are not in disk segments yet.
* **Retention** hides expired data at once and removes it from disk hourly by default (`Retention.CheckInterval`) or through `ApplyRetention()`; see [Retention](#retention).
* **Compaction.** Each store moves its in-memory segment to disk and merges in the background when the segment reaches `Storage.<store>.MutableSegmentMaxItemCount`. `await analytics.CompactAsync()` flushes and moves everything to disk now, and waits for the merges: useful after a bulk import to release memory, or to measure queries against data on disk.

---

## License

MIT. See [LICENSE](LICENSE).
