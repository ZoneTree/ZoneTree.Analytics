using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// End-to-end tests over a small, fully known web traffic scenario.
/// </summary>
/// <remarks>
/// Visitor A (10.0.0.1, Chrome/Windows, DE): /, /pricing (+1m), /signup (+3m). One session, not a bounce, 180 s.
/// Visitor B (10.0.0.2, Safari/iPhone, US): /pricing (+10m). One bounced session.
/// Visitor C (10.0.0.3, Safari/iPhone, DE): / from Google (+20m), then / again (+80m), a new session.
/// Totals: 6 page views, 3 visitors, 4 sessions, 3 bounces, 180 s total duration.
/// </remarks>
public sealed class EngineTests
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-10);

  static readonly DateTime T0 = Day0.AddHours(10);

  string directory;

  AnalyticsEngine engine;

  [SetUp]
  public void SetUp()
  {
    directory = TestEngine.NewDirectory("engine");
    engine = TestEngine.Create(directory);
  }

  [TearDown]
  public void TearDown()
  {
    engine?.Dispose();
    TestEngine.Delete(directory);
  }

  static AnalyticsEvent PageView(string url, string ip, string ua, DateTime timestamp, string referrer = null) =>
      new(StandardEvents.PageView)
      {
        Url = "https://mysite.com" + url,
        Referrer = referrer,
        IpAddress = ip,
        UserAgent = ua,
        Timestamp = timestamp
      };

  void TrackScenario(AnalyticsEngine target)
  {
    target.Track(PageView("/", "10.0.0.1", UserAgents.ChromeWindows, T0));
    target.Track(PageView("/pricing", "10.0.0.1", UserAgents.ChromeWindows, T0.AddMinutes(1)));
    target.Track(PageView("/signup", "10.0.0.1", UserAgents.ChromeWindows, T0.AddMinutes(3)));
    target.Track(PageView("/pricing", "10.0.0.2", UserAgents.SafariIPhone, T0.AddMinutes(10)));
    target.Track(PageView("/", "10.0.0.3", UserAgents.SafariIPhone, T0.AddMinutes(20), "https://www.google.com/"));
    target.Track(PageView("/", "10.0.0.3", UserAgents.SafariIPhone, T0.AddMinutes(80)));
  }

  AnalyticsResult Query(AnalyticsQuery query, QuerySource source)
  {
    query.Source = source;
    return engine.Query(query);
  }

  static readonly QuerySource[] Sources = [QuerySource.Rollups, QuerySource.RawEvents];

  [Test]
  public void SummaryMatchesScenario()
  {
    TrackScenario(engine);
    engine.Flush();
    var summary = engine.Web.GetSummary(Day0, Day0.AddDays(1));
    Assert.Multiple(() =>
    {
      Assert.That(summary.PageViews, Is.EqualTo(6));
      Assert.That(summary.Visitors, Is.EqualTo(3));
      Assert.That(summary.Sessions, Is.EqualTo(4));
      Assert.That(summary.BounceRate, Is.EqualTo(0.75).Within(1e-9));
      Assert.That(summary.AverageSessionDuration, Is.EqualTo(TimeSpan.FromSeconds(45)));
      Assert.That(summary.PageViewsPerSession, Is.EqualTo(1.5).Within(1e-9));
    });
    var statistics = engine.GetStatistics();
    Assert.Multiple(() =>
    {
      Assert.That(statistics.EventsProcessed, Is.EqualTo(6));
      Assert.That(statistics.SessionsStarted, Is.EqualTo(4));
      Assert.That(statistics.Errors, Is.EqualTo(0));
    });
  }

  [TestCaseSource(nameof(Sources))]
  public void SessionMetricsFromBothSources(QuerySource source)
  {
    TrackScenario(engine);
    engine.Flush();
    var result = Query(new AnalyticsQuery { Event = StandardEvents.Session }.Between(Day0, Day0.AddDays(1)), source);
    var totals = result.Totals;
    Assert.Multiple(() =>
    {
      Assert.That(totals.Events, Is.EqualTo(4), "sessions");
      Assert.That(totals.Visitors, Is.EqualTo(3));
      Assert.That(totals.Sum(StandardMetrics.Bounces), Is.EqualTo(3));
      Assert.That(totals.Sum(StandardMetrics.Duration), Is.EqualTo(180).Within(1e-6));
      Assert.That(totals.Sum(StandardMetrics.SessionPageViews), Is.EqualTo(6));
      Assert.That(result.UsedRollups, Is.EqualTo(source == QuerySource.Rollups));
      Assert.That(result.UsedRawEvents, Is.EqualTo(source == QuerySource.RawEvents));
    });
  }

  [TestCaseSource(nameof(Sources))]
  public void PagesWithEntrancesAndExits(QuerySource source)
  {
    TrackScenario(engine);
    engine.Flush();
    var rows = Query(new AnalyticsQuery()
        .Between(Day0, Day0.AddDays(1))
        .GroupBy(StandardDimensions.Page), source).Rows;
    var pages = rows.ToDictionary(x => x.Dimensions[0]);
    Assert.Multiple(() =>
    {
      Assert.That(rows.Select(x => x.Dimensions[0]), Is.EqualTo(new[] { "/", "/pricing", "/signup" }));
      Assert.That(pages["/"].Events, Is.EqualTo(3));
      Assert.That(pages["/"].Visitors, Is.EqualTo(2));
      Assert.That(pages["/"].Sum(StandardMetrics.Entrances), Is.EqualTo(3)); // A, C1, C2
      Assert.That(pages["/"].Sum(StandardMetrics.Exits), Is.EqualTo(2)); // C1, C2
      Assert.That(pages["/pricing"].Events, Is.EqualTo(2));
      Assert.That(pages["/pricing"].Visitors, Is.EqualTo(2));
      Assert.That(pages["/pricing"].Sum(StandardMetrics.Entrances), Is.EqualTo(1));
      Assert.That(pages["/pricing"].Sum(StandardMetrics.Exits), Is.EqualTo(1));
      Assert.That(pages["/signup"].Sum(StandardMetrics.Exits), Is.EqualTo(1));
    });
  }

  [TestCaseSource(nameof(Sources))]
  public void EntrancesCountEverySession(QuerySource source)
  {
    TrackScenario(engine);
    engine.Flush();
    var totals = Query(new AnalyticsQuery().Between(Day0, Day0.AddDays(1)), source).Totals;
    Assert.Multiple(() =>
    {
      // Every one of the 4 sessions starts with a page view and ends on one.
      Assert.That(totals.Sum(StandardMetrics.Entrances), Is.EqualTo(4));
      Assert.That(totals.Sum(StandardMetrics.Exits), Is.EqualTo(4));
    });
    // C's two sessions both enter and exit on "/" but are only counted
    // once each in the page row when grouped by page and session.
    var home = Query(new AnalyticsQuery()
        .Between(Day0, Day0.AddDays(1))
        .GroupBy(StandardDimensions.Page)
        .Where(StandardDimensions.Page, "/"), source);
    Assert.That(home.UsedRawEvents || home.UsedRollups, Is.True);
  }

  [TestCaseSource(nameof(Sources))]
  public void SessionBreakdownsByAcquisitionAndDevice(QuerySource source)
  {
    TrackScenario(engine);
    engine.Flush();
    AnalyticsRow[] Breakdown(string dimension) => Query(new AnalyticsQuery { Event = StandardEvents.Session }
        .Between(Day0, Day0.AddDays(1))
        .GroupBy(dimension)
        .Order(QueryOrder.ByEvents()), source).Rows.ToArray();

    var sources = Breakdown(StandardDimensions.Source);
    var devices = Breakdown(StandardDimensions.Device);
    var countries = Breakdown(StandardDimensions.Country);
    var channels = Breakdown(StandardDimensions.Channel);
    Assert.Multiple(() =>
    {
      Assert.That(sources.Select(x => (x.Dimensions[0], x.Events, x.Visitors)),
          Is.EqualTo(new[] { ("Direct", 3L, 3L), ("Google", 1L, 1L) }));
      Assert.That(channels.Select(x => x.Dimensions[0]), Is.EqualTo(new[] { "Direct", "Organic Search" }));
      Assert.That(devices.Select(x => (x.Dimensions[0], x.Events)),
          Is.EqualTo(new[] { ("Mobile", 3L), ("Desktop", 1L) }));
      Assert.That(countries.Select(x => (x.Dimensions[0], x.Events, x.Visitors)),
          Is.EqualTo(new[] { ("DE", 3L, 2L), ("US", 1L, 1L) }));
    });
  }

  [TestCaseSource(nameof(Sources))]
  public void HourlyTimeseries(QuerySource source)
  {
    TrackScenario(engine);
    engine.Flush();
    var rows = Query(new AnalyticsQuery()
        .Between(Day0, Day0.AddDays(1))
        .Every(TimeInterval.Hour), source).Rows;
    Assert.Multiple(() =>
    {
      Assert.That(rows, Has.Count.EqualTo(24));
      Assert.That(rows[10].Bucket, Is.EqualTo(new DateTimeOffset(T0, TimeSpan.Zero)));
      Assert.That(rows[10].Events, Is.EqualTo(5));
      Assert.That(rows[11].Events, Is.EqualTo(1));
      Assert.That(rows.Sum(x => x.Events), Is.EqualTo(6));
    });
  }

  [Test]
  public void DailyTimeseriesFillsEmptyDays()
  {
    TrackScenario(engine);
    engine.Flush();
    var rows = engine.Query(new AnalyticsQuery()
        .Between(Day0.AddDays(-1), Day0.AddDays(2))
        .Every(TimeInterval.Day)).Rows;
    Assert.Multiple(() =>
    {
      Assert.That(rows.Select(x => x.Events), Is.EqualTo(new long[] { 0, 6, 0 }));
      Assert.That(rows.Select(x => x.Bucket.Value.UtcDateTime),
          Is.EqualTo(new[] { Day0.AddDays(-1), Day0, Day0.AddDays(1) }));
    });
  }

  [Test]
  public void UnalignedRangeMixesRollupsAndRawEvents()
  {
    TrackScenario(engine);
    engine.Flush();
    var from = T0.AddMinutes(-30);
    var to = T0.AddMinutes(90);
    var mixed = engine.Query(new AnalyticsQuery().Between(from, to));
    var raw = engine.Query(new AnalyticsQuery { Source = QuerySource.RawEvents }.Between(from, to));
    Assert.Multiple(() =>
    {
      Assert.That(mixed.UsedRollups, Is.True);
      Assert.That(mixed.UsedRawEvents, Is.True);
      Assert.That(mixed.Totals.Events, Is.EqualTo(6));
      Assert.That(mixed.Totals.Visitors, Is.EqualTo(3));
      Assert.That(raw.Totals.Events, Is.EqualTo(6));
    });

    var sessions = engine.Query(new AnalyticsQuery { Event = StandardEvents.Session }.Between(from, to)).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(sessions.Events, Is.EqualTo(4));
      Assert.That(sessions.Sum(StandardMetrics.Bounces), Is.EqualTo(3));
    });
  }

  [Test]
  public void FiltersUseRollupsOrFallBackToRawEvents()
  {
    TrackScenario(engine);
    engine.Flush();
    var single = engine.Query(new AnalyticsQuery()
        .Between(Day0, Day0.AddDays(1))
        .Where(StandardDimensions.Country, "DE"));
    var combined = engine.Query(new AnalyticsQuery()
        .Between(Day0, Day0.AddDays(1))
        .GroupBy(StandardDimensions.Page)
        .Where(StandardDimensions.Country, "DE"));
    var negative = engine.Query(new AnalyticsQuery()
        .Between(Day0, Day0.AddDays(1))
        .GroupBy(StandardDimensions.Page)
        .Where(DimensionFilter.NotEqual(StandardDimensions.Page, "/")));
    Assert.Multiple(() =>
    {
      Assert.That(single.UsedRollups, Is.True);
      Assert.That(single.UsedRawEvents, Is.False);
      Assert.That(single.Totals.Events, Is.EqualTo(5));
      Assert.That(combined.UsedRollups, Is.False);
      Assert.That(combined.UsedRawEvents, Is.True);
      Assert.That(combined.Rows.Select(x => (x.Dimensions[0], x.Events)),
          Is.EqualTo(new[] { ("/", 3L), ("/pricing", 1L), ("/signup", 1L) }));
      Assert.That(negative.UsedRollups, Is.True);
      Assert.That(negative.Rows.Select(x => x.Dimensions[0]), Is.EqualTo(new[] { "/pricing", "/signup" }));
    });
  }

  [Test]
  public void WebReports()
  {
    TrackScenario(engine);
    engine.Flush();
    var from = Day0;
    var to = Day0.AddDays(1);
    var topPages = engine.Web.GetTopPages(from, to);
    var exits = engine.Web.GetExitPages(from, to);
    var entries = engine.Web.GetEntryPages(from, to);
    var browsers = engine.Web.GetBrowsers(from, to);
    var timeseries = engine.Web.GetTimeseries(from, to, TimeInterval.Hour);
    Assert.Multiple(() =>
    {
      Assert.That(topPages[0], Is.EqualTo(new Web.PageStats("/", 2, 3, 3, 2)));
      Assert.That(exits.Select(x => x.Page), Is.EqualTo(new[] { "/", "/pricing", "/signup" }));
      Assert.That(entries.Select(x => (x.Value, x.Sessions)), Is.EqualTo(new[] { ("/", 3L), ("/pricing", 1L) }));
      Assert.That(entries[0].BounceRate, Is.EqualTo(2.0 / 3).Within(1e-9));
      Assert.That(browsers.Select(x => x.Value), Is.EqualTo(new[] { "Safari", "Chrome" }));
      Assert.That(timeseries[10].Summary.PageViews, Is.EqualTo(5));
      Assert.That(timeseries[10].Summary.Sessions, Is.EqualTo(3));
      Assert.That(timeseries[11].Summary.Sessions, Is.EqualTo(1));
    });
  }

  [Test]
  public void CustomEventsAndMetrics()
  {
    engine.Dispose();
    TestEngine.Delete(directory);
    engine = TestEngine.Create(directory, options =>
        options.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] }));
    void Purchase(string user, string plan, double revenue, int minute) =>
        engine.Track(new AnalyticsEvent("purchase")
        {
          VisitorId = user,
          Timestamp = T0.AddMinutes(minute)
        }.WithDimension("plan", plan).WithMetric("revenue", revenue));
    Purchase("u1", "pro", 30, 0);
    Purchase("u2", "basic", 10, 1);
    Purchase("u1", "pro", 20, 2);
    engine.Track(new AnalyticsEvent("heartbeat") { Timestamp = T0 }.WithMetric("cpu", 0.5));
    engine.Flush();

    var totals = engine.Query(new AnalyticsQuery { Event = "purchase" }.Between(Day0, Day0.AddDays(1))).Totals;
    var byPlan = engine.Query(new AnalyticsQuery { Event = "purchase" }
        .Between(Day0, Day0.AddDays(1))
        .GroupBy("plan")
        .Order(QueryOrder.ByMetricSum("revenue")));
    var raw = engine.Query(new AnalyticsQuery { Event = "purchase", Source = QuerySource.RawEvents }
        .Between(Day0, Day0.AddDays(1))
        .GroupBy("plan")
        .Order(QueryOrder.ByMetricSum("revenue")));
    var revenue = totals.Metric("revenue");
    Assert.Multiple(() =>
    {
      Assert.That(totals.Events, Is.EqualTo(3));
      Assert.That(totals.Visitors, Is.EqualTo(2));
      Assert.That(revenue, Is.EqualTo(new MetricValue(60, 3, 10, 30)));
      Assert.That(revenue.Average, Is.EqualTo(20));
      Assert.That(byPlan.UsedRollups, Is.True);
      Assert.That(byPlan.Rows.Select(x => (x.Dimensions[0], x.Sum("revenue"))),
          Is.EqualTo(new[] { ("pro", 50.0), ("basic", 10.0) }));
      Assert.That(raw.Rows.Select(x => (x.Dimensions[0], x.Sum("revenue"))),
          Is.EqualTo(new[] { ("pro", 50.0), ("basic", 10.0) }));
      Assert.That(engine.GetEventNames(Day0, Day0.AddDays(1)),
          Is.EqualTo(new[] { "$session", "heartbeat", "purchase" }));
      Assert.That(engine.Web.GetCustomEvents(Day0, Day0.AddDays(1)).Select(x => (x.Name, x.Events)),
          Is.EqualTo(new[] { ("purchase", 3L), ("heartbeat", 1L) }));
    });
  }

  [Test]
  public void BotsAndInvalidEventsAreExcluded()
  {
    engine.Track(PageView("/", "10.0.0.9", UserAgents.Googlebot, T0));
    engine.Track(new AnalyticsEvent("") { Timestamp = T0 });
    engine.Track(new AnalyticsEvent("$session") { Timestamp = T0 });
    engine.Track(new AnalyticsEvent("future") { Timestamp = DateTime.UtcNow.AddDays(2) });
    engine.Track(PageView("/", "10.0.0.1", UserAgents.ChromeWindows, T0));
    engine.Flush();
    var statistics = engine.GetStatistics();
    Assert.Multiple(() =>
    {
      Assert.That(statistics.BotsExcluded, Is.EqualTo(1));
      Assert.That(statistics.EventsRejected, Is.EqualTo(3));
      Assert.That(statistics.EventsProcessed, Is.EqualTo(1));
    });
  }

  [Test]
  public void SitesAreIsolated()
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Site = "a.com", Url = "/", VisitorId = "1", Timestamp = T0 });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Site = "a.com", Url = "/x", VisitorId = "2", Timestamp = T0 });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Site = "b.com", Url = "/", VisitorId = "1", Timestamp = T0 });
    engine.Flush();
    foreach (var source in Sources)
    {
      var a = engine.Query(new AnalyticsQuery { Site = "a.com", Source = source }.Between(Day0, Day0.AddDays(1))).Totals;
      var b = engine.Query(new AnalyticsQuery { Site = "b.com", Source = source }.Between(Day0, Day0.AddDays(1))).Totals;
      var other = engine.Query(new AnalyticsQuery { Source = source }.Between(Day0, Day0.AddDays(1))).Totals;
      Assert.Multiple(() =>
      {
        Assert.That(a.Events, Is.EqualTo(2));
        Assert.That(b.Events, Is.EqualTo(1));
        Assert.That(other.Events, Is.EqualTo(0));
      });
    }
  }

  [Test]
  public void DataSurvivesRestartAndSessionsContinue()
  {
    TrackScenario(engine);
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "alice", Timestamp = T0 });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/a", VisitorId = "alice", Timestamp = T0.AddMinutes(3) });
    engine.Dispose();
    engine = TestEngine.Create(directory);
    var summary = engine.Web.GetSummary(Day0, Day0.AddDays(1));
    Assert.Multiple(() =>
    {
      Assert.That(summary.PageViews, Is.EqualTo(8));
      Assert.That(summary.Sessions, Is.EqualTo(5));
    });

    // Alice continues the same session after the restart. Anonymous visitors of past days
    // cannot continue: their daily salt is destroyed by design.
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/b", VisitorId = "alice", Timestamp = T0.AddMinutes(5) });
    engine.Flush();
    var alice = engine.Query(new AnalyticsQuery { Event = StandardEvents.Session }
        .Between(Day0, Day0.AddDays(1))
        .Where(StandardDimensions.EntryPage, "/")
        .Where(StandardDimensions.Device, "")).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(alice.Events, Is.EqualTo(1));
      Assert.That(alice.Sum(StandardMetrics.Duration), Is.EqualTo(300).Within(1e-6));
      Assert.That(alice.Sum(StandardMetrics.SessionPageViews), Is.EqualTo(3));
      Assert.That(alice.Sum(StandardMetrics.Bounces), Is.EqualTo(0));
      Assert.That(engine.GetStatistics().SessionsStarted, Is.EqualTo(0));
    });
  }

  [Test]
  public void StorageFormatSettingsCannotChange()
  {
    engine.Dispose();
    engine = null;
    Assert.Throws<InvalidOperationException>(() =>
        TestEngine.Create(directory, options => options.Rollups.HyperLogLogPrecision = 12).Dispose());
    engine = TestEngine.Create(directory);
  }

  [Test]
  public void CurrentVisitorsAreRealTime()
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "1" });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "2" });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "2" });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "3", Timestamp = DateTime.UtcNow.AddHours(-1) });
    engine.Flush();
    Assert.That(engine.Web.GetCurrentVisitors(), Is.EqualTo(2));
  }

  [Test]
  public void ConcurrentIngestionWithSeveralWorkers()
  {
    engine.Dispose();
    TestEngine.Delete(directory);
    engine = TestEngine.Create(directory, options => options.Ingestion.WorkerCount = 4);
    const int threads = 8;
    const int perThread = 5_000;
    Parallel.For(0, threads, t =>
    {
      for (var i = 0; i < perThread; ++i)
      {
        engine.Track(new AnalyticsEvent(StandardEvents.PageView)
        {
          Url = "/p" + (i % 10),
          VisitorId = "v" + (i % 1000),
          Timestamp = T0.AddSeconds(i % 3600)
        });
      }
    });
    engine.Flush();
    var totals = engine.Query(new AnalyticsQuery().Between(Day0, Day0.AddDays(1))).Totals;
    var sessions = engine.Query(new AnalyticsQuery { Event = StandardEvents.Session }.Between(Day0, Day0.AddDays(1))).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(engine.GetStatistics().EventsProcessed, Is.EqualTo(threads * perThread));
      Assert.That(totals.Events, Is.EqualTo(threads * perThread));
      Assert.That(totals.Visitors, Is.EqualTo(1000));
      Assert.That(sessions.Sum(StandardMetrics.SessionPageViews), Is.EqualTo(threads * perThread));
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(0));
    });
  }

  [Test]
  public void ReportingTimeZoneDefinesDays()
  {
    engine.Dispose();
    TestEngine.Delete(directory);
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    engine = TestEngine.Create(directory, options => options.TimeZone = zone);
    // 03:00 UTC is 22:00 of the previous day in New York (EST, UTC-5).
    var utcDay = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = utcDay.AddHours(3) });
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = utcDay.AddHours(6) });
    engine.Flush();
    foreach (var source in Sources)
    {
      var rows = engine.Query(new AnalyticsQuery { Event = "e", Source = source }
          .Between(utcDay.AddDays(-1), utcDay.AddDays(1))
          .Every(TimeInterval.Day)).Rows.Where(x => x.Events > 0).ToArray();
      Assert.Multiple(() =>
      {
        Assert.That(rows, Has.Length.EqualTo(2), source.ToString());
        Assert.That(rows[0].Bucket.Value.DateTime, Is.EqualTo(new DateTime(2026, 1, 14)));
        Assert.That(rows[0].Bucket.Value.Offset, Is.EqualTo(TimeSpan.FromHours(-5)));
        Assert.That(rows[1].Bucket.Value.DateTime, Is.EqualTo(new DateTime(2026, 1, 15)));
      });
    }
  }

  [Test]
  public void RetentionDropsExpiredPartitions()
  {
    engine.Dispose();
    TestEngine.Delete(directory);
    engine = TestEngine.Create(directory, options =>
    {
      options.Retention.RawEvents = TimeSpan.FromDays(30);
      options.Retention.HourRollups = TimeSpan.FromDays(60);
    });
    var old = DateTime.UtcNow.Date.AddDays(-200).AddHours(12);
    var recent = DateTime.UtcNow.Date.AddDays(-1).AddHours(12);
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = old });
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = recent });
    engine.Flush();
    var dropped = engine.ApplyRetention();
    Assert.That(dropped, Is.EqualTo(2), "raw and hourly partitions of the old month");

    // Daily rollups are kept forever and still answer the old day.
    var oldDay = engine.Query(new AnalyticsQuery { Event = "e" }.Between(old.Date, old.Date.AddDays(1)));
    var oldRaw = engine.Query(new AnalyticsQuery { Event = "e", Source = QuerySource.RawEvents }.Between(old.Date, old.Date.AddDays(1)));
    var recentHour = engine.Query(new AnalyticsQuery { Event = "e" }.Between(recent.AddHours(-1), recent.AddHours(1)));
    Assert.Multiple(() =>
    {
      Assert.That(oldDay.Totals.Events, Is.EqualTo(1));
      Assert.That(oldDay.UsedRawEvents, Is.False);
      Assert.That(oldRaw.Totals.Events, Is.EqualTo(0));
      Assert.That(oldRaw.MayBeIncomplete, Is.True);
      Assert.That(recentHour.Totals.Events, Is.EqualTo(1));
    });
  }

  [Test]
  public void RawEventsNeverContainIpOrUserAgent()
  {
    TrackScenario(engine);
    engine.Flush();
    var store = new List<RawEvent>();
    var options = TestEngine.Options(directory);
    engine.Dispose();
    engine = null;
    using (var raw = new RawEventStore(
        Path.Combine(directory, "events"),
        PartitionPeriod.Month,
        new StoreFactory(options.Storage, new ZoneTree.Logger.ConsoleLogger()),
        new ZoneTree.Logger.ConsoleLogger()))
    {
      raw.Scan("default", Day0.Ticks, Day0.AddDays(1).Ticks, (ticks, _, value) =>
      {
        var e = new RawEvent();
        RawEventCodec.TryDecode(value.Span, ticks, null, default, e);
        store.Add(e);
        return true;
      });
    }
    var allValues = store.SelectMany(x => x.Dimensions.Select(d => d.Value)).ToList();
    Assert.Multiple(() =>
    {
      Assert.That(store, Has.Count.EqualTo(6));
      Assert.That(allValues, Has.None.Contains("10.0.0."));
      Assert.That(allValues, Has.None.Contains("Mozilla"));
      Assert.That(store.Select(x => x.Visitor).Distinct().Count(), Is.EqualTo(3));
    });
    engine = TestEngine.Create(directory);
  }

  [Test]
  public async Task EraseVisitorRemovesRawEvents()
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "alice", Timestamp = T0 });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/a", VisitorId = "alice", Timestamp = T0.AddMinutes(1) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "bob", Timestamp = T0 });
    var deleted = await engine.EraseVisitorAsync("alice");
    var raw = engine.Query(new AnalyticsQuery { Source = QuerySource.RawEvents }.Between(Day0, Day0.AddDays(1))).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(deleted, Is.EqualTo(2));
      Assert.That(raw.Events, Is.EqualTo(1));
    });
  }

  [Test]
  public void ExplicitOnlyIdentificationIgnoresFingerprints()
  {
    engine.Dispose();
    TestEngine.Delete(directory);
    engine = TestEngine.Create(directory, options =>
        options.Privacy.VisitorIdentification = VisitorIdentification.ExplicitOnly);
    TrackScenario(engine);
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "u", Timestamp = T0 });
    engine.Flush();
    var totals = engine.Query(new AnalyticsQuery().Between(Day0, Day0.AddDays(1))).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(totals.Events, Is.EqualTo(7));
      Assert.That(totals.Visitors, Is.EqualTo(1));
    });
  }

  [Test]
  public void QueriesWithoutRollupAndWithoutRawEventsFail()
  {
    engine.Dispose();
    TestEngine.Delete(directory);
    engine = TestEngine.Create(directory, options => options.Storage.StoreRawEvents = false);
    Assert.Throws<AnalyticsQueryException>(() => engine.Query(new AnalyticsQuery()
        .Between(Day0, Day0.AddDays(1))
        .GroupBy(StandardDimensions.Page, StandardDimensions.Country)));
  }
}
