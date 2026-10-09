using ZoneTree.Analytics.Storage;
using ZoneTree.Analytics.Web;

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

  static readonly QuerySource[] Sources = [QuerySource.Rollups, QuerySource.RawEvents];

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

  void Recreate(Action<AnalyticsOptions> configure)
  {
    engine.Dispose();
    TestEngine.Delete(directory);
    engine = TestEngine.Create(directory, configure);
  }

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  static DateRange Range(DateTime from, DateTime to) => DateRange.Between(Utc(from), Utc(to));

  static DateRange Day(DateTime day) => Range(day, day.AddDays(1));

  static AnalyticsEvent PageView(string url, string ip, string ua, DateTime timestamp, string referrer = null) =>
      new(StandardEvents.PageView)
      {
        Url = "https://mysite.com" + url,
        Referrer = referrer,
        IpAddress = ip,
        UserAgent = ua,
        Timestamp = Utc(timestamp)
      };

  void TrackScenario()
  {
    engine.Track(PageView("/", "10.0.0.1", UserAgents.ChromeWindows, T0));
    engine.Track(PageView("/pricing", "10.0.0.1", UserAgents.ChromeWindows, T0.AddMinutes(1)));
    engine.Track(PageView("/signup", "10.0.0.1", UserAgents.ChromeWindows, T0.AddMinutes(3)));
    engine.Track(PageView("/pricing", "10.0.0.2", UserAgents.SafariIPhone, T0.AddMinutes(10)));
    engine.Track(PageView("/", "10.0.0.3", UserAgents.SafariIPhone, T0.AddMinutes(20), "https://www.google.com/"));
    engine.Track(PageView("/", "10.0.0.3", UserAgents.SafariIPhone, T0.AddMinutes(80)));
  }

  AnalyticsResult Query(Action<AnalyticsQuery> configure)
  {
    var query = new AnalyticsQuery { Range = Day(Day0) };
    configure(query);
    return engine.Query(query);
  }

  [Test]
  public async Task WebSummaryMatchesScenario()
  {
    TrackScenario();
    engine.Flush();
    var summary = await engine.Web.GetSummaryAsync(new WebReportRequest { Range = Day(Day0) });
    Assert.Multiple(() =>
    {
      Assert.That(summary.PageViews, Is.EqualTo(6));
      Assert.That(summary.Visitors, Is.EqualTo(3));
      Assert.That(summary.Sessions, Is.EqualTo(4));
      Assert.That(summary.BounceRate, Is.EqualTo(0.75).Within(1e-9));
      Assert.That(summary.AverageSessionDuration, Is.EqualTo(TimeSpan.FromSeconds(45)));
      Assert.That(summary.PageViewsPerSession, Is.EqualTo(1.5).Within(1e-9));
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(0));
    });
  }

  [TestCaseSource(nameof(Sources))]
  public void SessionMetricsFromBothSources(QuerySource source)
  {
    TrackScenario();
    engine.Flush();
    var result = Query(q =>
    {
      q.Source = source;
      q.Metrics.AddRange([
        Metric.Sessions, Metric.Bounces, Metric.BounceRate, Metric.SessionDuration,
        Metric.AverageSessionDuration, Metric.PageViewsPerSession, Metric.VisitorsOf(StandardEvents.Session)]);
    });
    var totals = result.Totals;
    Assert.Multiple(() =>
    {
      Assert.That(totals[Metric.Sessions], Is.EqualTo(4));
      Assert.That(totals[Metric.Bounces], Is.EqualTo(3));
      Assert.That(totals[Metric.BounceRate], Is.EqualTo(0.75).Within(1e-9));
      Assert.That(totals[Metric.SessionDuration], Is.EqualTo(180).Within(1e-6));
      Assert.That(totals[Metric.AverageSessionDuration], Is.EqualTo(45).Within(1e-6));
      Assert.That(totals[Metric.PageViewsPerSession], Is.EqualTo(1.5).Within(1e-9));
      Assert.That(totals[Metric.VisitorsOf(StandardEvents.Session)], Is.EqualTo(3));
      Assert.That(result.Plan.UsedRollups, Is.EqualTo(source == QuerySource.Rollups));
      Assert.That(result.Plan.UsedRawEvents, Is.EqualTo(source == QuerySource.RawEvents));
      Assert.That(result.Plan.IsComplete, Is.True);
    });
  }

  [TestCaseSource(nameof(Sources))]
  public void PagesWithEntrancesAndExits(QuerySource source)
  {
    TrackScenario();
    engine.Flush();
    var result = Query(q =>
    {
      q.Source = source;
      q.GroupBy.Add(StandardDimensions.Page);
      q.Metrics.AddRange([Metric.PageViews, Metric.Visitors, Metric.Entrances, Metric.Exits, Metric.ExitRate]);
    });
    var rows = result.Rows.Select(x => (
        x[StandardDimensions.Page],
        x[Metric.PageViews], x[Metric.Visitors], x[Metric.Entrances], x[Metric.Exits]));
    Assert.That(rows, Is.EqualTo(new[]
    {
      ("/", 3.0, 2.0, 3.0, 2.0),
      ("/pricing", 2.0, 2.0, 1.0, 1.0),
      ("/signup", 1.0, 1.0, 0.0, 1.0),
    }));
    Assert.That(result.Rows[1][Metric.ExitRate], Is.EqualTo(0.5));
  }

  [TestCaseSource(nameof(Sources))]
  public void OneQueryJoinsPageViewsSessionsAndGoals(QuerySource source)
  {
    TrackScenario();
    engine.Track(new AnalyticsEvent("signup")
    {
      Url = "https://mysite.com/signup",
      IpAddress = "10.0.0.1",
      UserAgent = UserAgents.ChromeWindows,
      Timestamp = Utc(T0.AddMinutes(4))
    });
    engine.Flush();
    var share = Metric.PercentOfTotal(Metric.Visitors);
    var result = Query(q =>
    {
      q.Source = source;
      q.GroupBy.Add(StandardDimensions.Source);
      q.Metrics.AddRange([Metric.Visitors, Metric.PageViews, Metric.Sessions, Metric.BounceRate, Metric.ConversionRate("signup"), share]);
    });
    var direct = result.Rows[0];
    var google = result.Rows[1];
    Assert.Multiple(() =>
    {
      Assert.That(result.Rows.Select(x => x[StandardDimensions.Source]), Is.EqualTo(new[] { "Direct", "Google" }));
      Assert.That(direct[Metric.Visitors], Is.EqualTo(3));
      Assert.That(direct[Metric.PageViews], Is.EqualTo(5));
      Assert.That(direct[Metric.Sessions], Is.EqualTo(3));
      Assert.That(direct[Metric.BounceRate], Is.EqualTo(2.0 / 3).Within(1e-9));
      Assert.That(direct[Metric.ConversionRate("signup")], Is.EqualTo(1.0 / 3).Within(1e-9));
      Assert.That(direct[share], Is.EqualTo(1.0));
      Assert.That(google[Metric.Visitors], Is.EqualTo(1));
      Assert.That(google[Metric.BounceRate], Is.EqualTo(1.0));
      Assert.That(google[Metric.ConversionRate("signup")], Is.EqualTo(0));
      Assert.That(google[share], Is.EqualTo(1.0 / 3).Within(1e-9));
      Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(3));
      Assert.That(result.Totals[share], Is.EqualTo(1.0));
    });
  }

  [TestCaseSource(nameof(Sources))]
  public void MissingDimensionsGroupUnderNull(QuerySource source)
  {
    Recreate(o => o.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] }));
    engine.Track(new AnalyticsEvent("purchase") { VisitorId = "1", Timestamp = Utc(T0), Dimensions = { ["plan"] = "pro" } });
    engine.Track(new AnalyticsEvent("purchase") { VisitorId = "2", Timestamp = Utc(T0), Dimensions = { ["plan"] = "pro" } });
    engine.Track(new AnalyticsEvent("purchase") { VisitorId = "3", Timestamp = Utc(T0) });
    engine.Flush();
    AnalyticsResult Run(params Filter[] filters) => Query(q =>
    {
      q.Source = source;
      q.Event = "purchase";
      q.Filters.AddRange(filters);
      q.GroupBy.Add("plan");
    });
    var grouped = Run();
    Assert.Multiple(() =>
    {
      Assert.That(grouped.Rows.Select(x => (x["plan"], x[Metric.Events])),
          Is.EqualTo(new[] { ("pro", 2.0), ((string)null, 1.0) }));
      Assert.That(grouped.Totals[Metric.Events], Is.EqualTo(3));
      Assert.That(grouped.Totals[Metric.Visitors], Is.EqualTo(3));
      Assert.That(Run(Filter.IsNotSet("plan")).Totals[Metric.Events], Is.EqualTo(1));
      Assert.That(Run(Filter.IsSet("plan")).Totals[Metric.Events], Is.EqualTo(2));
      Assert.That(Run(Filter.NotEqual("plan", "pro")).Totals[Metric.Events], Is.EqualTo(1));
      Assert.That(Run(Filter.Equal("plan", "pro")).Totals[Metric.Events], Is.EqualTo(2));
      Assert.That(Run(Filter.Matches("plan", "^p")).Totals[Metric.Events], Is.EqualTo(2));
    });
  }

  [Test]
  public void MetricScopeIsValidated()
  {
    var exception = Assert.Throws<AnalyticsQueryException>(() => Query(q =>
    {
      q.GroupBy.Add(StandardDimensions.Page);
      q.Metrics.Add(Metric.BounceRate);
    }));
    Assert.That(exception.Message, Does.Contain("bounce_rate").And.Contain("Segment"));
    Assert.Throws<AnalyticsQueryException>(() => Query(q =>
    {
      q.Metrics.Add(Metric.Visitors);
      q.OrderBy.Add(Order.By(Metric.PageViews));
    }));
  }

  [TestCaseSource(nameof(Sources))]
  public void HourlyTimeseries(QuerySource source)
  {
    TrackScenario();
    engine.Flush();
    var rows = Query(q =>
    {
      q.Source = source;
      q.Interval = TimeInterval.Hour;
      q.Metrics.AddRange([Metric.PageViews, Metric.Sessions]);
    }).Rows;
    Assert.Multiple(() =>
    {
      Assert.That(rows, Has.Count.EqualTo(24));
      Assert.That(rows[10].Bucket, Is.EqualTo(Utc(T0)));
      Assert.That(rows[10][Metric.PageViews], Is.EqualTo(5));
      Assert.That(rows[10][Metric.Sessions], Is.EqualTo(3));
      Assert.That(rows[11][Metric.PageViews], Is.EqualTo(1));
      Assert.That(rows[11][Metric.Sessions], Is.EqualTo(1));
    });
  }

  [Test]
  public void DailyTimeseriesFillsEmptyDays()
  {
    TrackScenario();
    engine.Flush();
    var rows = Query(q =>
    {
      q.Range = Range(Day0.AddDays(-1), Day0.AddDays(2));
      q.Interval = TimeInterval.Day;
    }).Rows;
    Assert.Multiple(() =>
    {
      Assert.That(rows.Select(x => x[Metric.Events]), Is.EqualTo(new double[] { 0, 6, 0 }));
      Assert.That(rows.Select(x => x.Bucket.Value), Is.EqualTo(new[] { Utc(Day0.AddDays(-1)), Utc(Day0), Utc(Day0.AddDays(1)) }));
    });
  }

  [Test]
  public void UnalignedRangeMixesRollupsAndRawEvents()
  {
    TrackScenario();
    engine.Flush();
    var result = Query(q =>
    {
      q.Range = Range(T0.AddMinutes(-30), T0.AddMinutes(90));
      q.Metrics.AddRange([Metric.PageViews, Metric.Visitors, Metric.Sessions, Metric.Bounces]);
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Plan.UsedRollups, Is.True);
      Assert.That(result.Plan.UsedRawEvents, Is.True);
      Assert.That(result.Plan.Steps.Any(x => x.Source == PlanSource.HourRollups), Is.True);
      Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(6));
      Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(3));
      Assert.That(result.Totals[Metric.Sessions], Is.EqualTo(4));
      Assert.That(result.Totals[Metric.Bounces], Is.EqualTo(3));
    });
  }

  [Test]
  public void FiltersUseRollupsOrFallBackToRawEvents()
  {
    TrackScenario();
    engine.Flush();
    var single = Query(q => q.Filters.Add(Filter.Equal(StandardDimensions.Country, "DE")));
    var combined = Query(q =>
    {
      q.GroupBy.Add(StandardDimensions.Page);
      q.Filters.Add(Filter.Equal(StandardDimensions.Country, "DE"));
    });
    var negative = Query(q =>
    {
      q.GroupBy.Add(StandardDimensions.Page);
      q.Filters.Add(Filter.NotEqual(StandardDimensions.Page, "/"));
    });
    Assert.Multiple(() =>
    {
      Assert.That(single.Plan.UsedRawEvents, Is.False);
      Assert.That(single.Totals[Metric.Events], Is.EqualTo(5));
      Assert.That(combined.Plan.UsedRollups, Is.False);
      Assert.That(combined.Rows.Select(x => (x[StandardDimensions.Page], x[Metric.Events])),
          Is.EqualTo(new[] { ("/", 3.0), ("/pricing", 1.0), ("/signup", 1.0) }));
      Assert.That(negative.Plan.UsedRawEvents, Is.False);
      Assert.That(negative.Rows.Select(x => x[StandardDimensions.Page]), Is.EqualTo(new[] { "/pricing", "/signup" }));
    });
  }

  [Test]
  public void PagingAndOrdering()
  {
    TrackScenario();
    engine.Flush();
    var page = Query(q =>
    {
      q.GroupBy.Add(StandardDimensions.Page);
      q.OrderBy.Add(Order.ByDimension(StandardDimensions.Page, descending: true));
      q.Offset = 1;
      q.Limit = 1;
    });
    Assert.Multiple(() =>
    {
      Assert.That(page.TotalRows, Is.EqualTo(3));
      Assert.That(page.Rows.Single()[StandardDimensions.Page], Is.EqualTo("/pricing"));
      Assert.That(page.Totals[Metric.Events], Is.EqualTo(6));
    });
  }

  [Test]
  public void ComparisonWithPreviousPeriod()
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/a", VisitorId = "1", Timestamp = Utc(T0) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/a", VisitorId = "2", Timestamp = Utc(T0) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/b", VisitorId = "1", Timestamp = Utc(T0.AddDays(-1)) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/a", VisitorId = "1", Timestamp = Utc(T0.AddDays(-1)) });
    engine.Flush();
    var grouped = Query(q =>
    {
      q.GroupBy.Add(StandardDimensions.Page);
      q.Compare = Comparison.PreviousPeriod;
    });
    var hourly = Query(q =>
    {
      q.Interval = TimeInterval.Hour;
      q.Compare = Comparison.PreviousPeriod;
    });
    var a = grouped.Rows.Single(x => x[StandardDimensions.Page] == "/a");
    Assert.Multiple(() =>
    {
      Assert.That(grouped.Totals[Metric.Events], Is.EqualTo(2));
      Assert.That(grouped.Totals.Previous[Metric.Events], Is.EqualTo(2));
      Assert.That(a[Metric.Events], Is.EqualTo(2));
      Assert.That(a.Previous[Metric.Events], Is.EqualTo(1));
      Assert.That(a.Change(Metric.Events), Is.EqualTo(1.0));
      Assert.That(grouped.Comparison.Rows.Select(x => x[StandardDimensions.Page]), Is.EquivalentTo(new[] { "/a", "/b" }));
      Assert.That(hourly.Rows[10][Metric.Events], Is.EqualTo(2));
      Assert.That(hourly.Rows[10].Previous[Metric.Events], Is.EqualTo(2));
      Assert.That(hourly.Rows[10].Previous.Bucket, Is.EqualTo(Utc(T0.AddDays(-1))));
    });
  }

  [Test]
  public void CalendarRanges()
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "1" });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "1", Timestamp = DateTimeOffset.UtcNow.AddDays(-1) });
    engine.Flush();
    double Count(DateRange range) => engine.Query(new AnalyticsQuery { Range = range }).Totals[Metric.Events];
    Assert.Multiple(() =>
    {
      Assert.That(Count(DateRange.Today), Is.EqualTo(1));
      Assert.That(Count(DateRange.Yesterday), Is.EqualTo(1));
      Assert.That(Count(DateRange.LastDays(2)), Is.EqualTo(2));
      Assert.That(Count(DateRange.LastDays(1, includeToday: false)), Is.EqualTo(1));
      Assert.That(Count(DateRange.Last(TimeSpan.FromMinutes(5))), Is.EqualTo(1));
      Assert.That(Count(DateRange.ThisYear), Is.GreaterThanOrEqualTo(1));
    });
  }

  [Test]
  public void QueryTimeZoneOverridesReportingTimeZone()
  {
    var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    var utcDay = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(utcDay.AddHours(3)) });
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(utcDay.AddHours(6)) });
    engine.Flush();
    foreach (var source in Sources)
    {
      var result = engine.Query(new AnalyticsQuery
      {
        Event = "e",
        Source = source,
        TimeZone = newYork,
        Range = DateRange.Days(new DateOnly(2026, 1, 14), new DateOnly(2026, 1, 15)),
        Interval = TimeInterval.Day
      });
      Assert.Multiple(() =>
      {
        Assert.That(result.Rows.Select(x => x[Metric.Events]), Is.EqualTo(new double[] { 1, 1 }), source.ToString());
        Assert.That(result.Rows[0].Bucket.Value.Offset, Is.EqualTo(TimeSpan.FromHours(-5)));
        Assert.That(result.Plan.Steps.Any(x => x.Source == PlanSource.DayRollups), Is.False);
        Assert.That(result.From, Is.EqualTo(new DateTimeOffset(2026, 1, 14, 0, 0, 0, TimeSpan.FromHours(-5))));
      });
    }
  }

  [Test]
  public void ReportingTimeZoneDefinesDays()
  {
    Recreate(o => o.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
    var utcDay = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(utcDay.AddHours(3)) });
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(utcDay.AddHours(6)) });
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "e",
      Range = DateRange.Days(new DateOnly(2026, 1, 14), new DateOnly(2026, 1, 15)),
      Interval = TimeInterval.Day
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Rows.Select(x => x[Metric.Events]), Is.EqualTo(new double[] { 1, 1 }));
      Assert.That(result.Plan.Steps.All(x => x.Source == PlanSource.DayRollups), Is.True);
    });
  }

  [Test]
  public void SegmentsSelectVisitorsAndSessions()
  {
    TrackScenario();
    engine.Flush();
    var converted = Query(q =>
    {
      q.Segments.Add(Segment.VisitorsWhoViewed("/signup"));
      q.Metrics.AddRange([Metric.PageViews, Metric.Visitors]);
    });
    var others = Query(q =>
    {
      q.Segments.Add(Segment.VisitorsWhoViewed("/signup").Exclude());
      q.Metrics.AddRange([Metric.PageViews, Metric.Visitors]);
    });
    var pricingSessions = Query(q =>
    {
      q.Segments.Add(Segment.SessionsWith(StandardEvents.PageView, Filter.Equal(StandardDimensions.Page, "/pricing")));
      q.Metrics.AddRange([Metric.Sessions, Metric.BounceRate]);
    });
    Assert.Multiple(() =>
    {
      Assert.That(converted.Totals[Metric.PageViews], Is.EqualTo(3));
      Assert.That(converted.Totals[Metric.Visitors], Is.EqualTo(1));
      Assert.That(converted.Plan.UsedRollups, Is.False);
      Assert.That(others.Totals[Metric.PageViews], Is.EqualTo(3));
      Assert.That(others.Totals[Metric.Visitors], Is.EqualTo(2));
      Assert.That(pricingSessions.Totals[Metric.Sessions], Is.EqualTo(2));
      Assert.That(pricingSessions.Totals[Metric.BounceRate], Is.EqualTo(0.5));
    });
  }

  [Test]
  public async Task Funnels()
  {
    TrackScenario();
    engine.Flush();
    FunnelQuery Funnel(TimeSpan? window)
    {
      var funnel = new FunnelQuery { Range = Day(Day0), Window = window };
      funnel.Steps.Add(FunnelStep.Page("Home", "/"));
      funnel.Steps.Add(FunnelStep.Page("Pricing", "/pricing"));
      funnel.Steps.Add(FunnelStep.Page("Signup", "/signup"));
      return funnel;
    }
    var bySession = await engine.QueryFunnelAsync(Funnel(null));
    var byWindow = await engine.QueryFunnelAsync(Funnel(TimeSpan.FromMinutes(2)));
    var safariFunnel = Funnel(null);
    safariFunnel.Filters.Add(Filter.Equal(StandardDimensions.Browser, "Safari"));
    var safari = await engine.QueryFunnelAsync(safariFunnel);
    Assert.Multiple(() =>
    {
      Assert.That(bySession.Steps.Select(x => x.Visitors), Is.EqualTo(new long[] { 2, 1, 1 }));
      Assert.That(bySession.Steps[1].StepConversionRate, Is.EqualTo(0.5));
      Assert.That(bySession.Steps[1].DropOff, Is.EqualTo(1));
      Assert.That(bySession.ConversionRate, Is.EqualTo(0.5));
      // /signup came 3 minutes after the first step: outside a 2 minute window.
      Assert.That(byWindow.Steps.Select(x => x.Visitors), Is.EqualTo(new long[] { 2, 1, 0 }));
      Assert.That(safari.Steps.Select(x => x.Visitors), Is.EqualTo(new long[] { 1, 0, 0 }));
    });
    Assert.ThrowsAsync<AnalyticsQueryException>(async () => await engine.QueryFunnelAsync(new FunnelQuery()));
  }

  [Test]
  public async Task WebReports()
  {
    TrackScenario();
    engine.Track(new AnalyticsEvent("signup") { IpAddress = "10.0.0.1", UserAgent = UserAgents.ChromeWindows, Timestamp = Utc(T0.AddMinutes(4)) });
    engine.Flush();
    var request = new WebReportRequest { Range = Day(Day0) };
    var pages = await engine.Web.GetPagesAsync(request);
    var exits = await engine.Web.GetExitPagesAsync(request);
    var entries = await engine.Web.GetBreakdownAsync(request, StandardDimensions.EntryPage);
    var browsers = await engine.Web.GetBreakdownAsync(request, StandardDimensions.Browser);
    var byPage = await engine.Web.GetBreakdownAsync(request, StandardDimensions.Page);
    var goals = await engine.Web.GetGoalsAsync(request);
    var timeseries = await engine.Web.GetTimeseriesAsync(request, TimeInterval.Hour);
    Assert.Multiple(() =>
    {
      Assert.That(pages.Rows[0], Is.EqualTo(new PageStats("/", 2, 3, 3, 2, 2.0 / 3)));
      Assert.That(pages.TotalRows, Is.EqualTo(3));
      Assert.That(exits.Rows.Select(x => x.Page), Is.EqualTo(new[] { "/", "/pricing", "/signup" }));
      Assert.That(entries.Rows.Select(x => (x.Value, x.Sessions)), Is.EqualTo(new[] { ("/", (long?)3), ("/pricing", 1) }));
      Assert.That(entries.Rows[0].BounceRate, Is.EqualTo(2.0 / 3).Within(1e-9));
      Assert.That(browsers.Rows.Select(x => (x.Value, x.Visitors)), Is.EqualTo(new[] { ("Safari", 2L), ("Chrome", 1L) }));
      Assert.That(browsers.Rows[0].VisitorShare, Is.EqualTo(2.0 / 3).Within(1e-9));
      Assert.That(byPage.Rows[0].Sessions, Is.Null);
      Assert.That(goals.Rows.Single(), Is.EqualTo(new GoalStats("signup", 1, 1, 1.0 / 3)));
      Assert.That(timeseries[10].Summary.PageViews, Is.EqualTo(5));
      Assert.That(timeseries[10].Summary.Sessions, Is.EqualTo(3));
    });
  }

  [Test]
  public void CustomEventsAndMetrics()
  {
    Recreate(o => o.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] }));
    void Purchase(string user, string plan, double revenue, int minute) =>
        engine.Track(new AnalyticsEvent("purchase") { VisitorId = user, Timestamp = Utc(T0.AddMinutes(minute)) }
            .WithDimension("plan", plan)
            .WithMetric("revenue", revenue));
    Purchase("u1", "pro", 30, 0);
    Purchase("u2", "basic", 10, 1);
    Purchase("u1", "pro", 20, 2);
    engine.Track(new AnalyticsEvent("heartbeat") { Timestamp = Utc(T0) }.WithMetric("cpu", 0.5));
    engine.Flush();
    Metric[] revenue = [Metric.Sum("revenue"), Metric.Average("revenue"), Metric.Min("revenue"), Metric.Max("revenue"), Metric.Count("revenue")];
    foreach (var source in Sources)
    {
      var result = Query(q =>
      {
        q.Source = source;
        q.Event = "purchase";
        q.GroupBy.Add("plan");
        q.Metrics.Add(Metric.Events);
        q.Metrics.Add(Metric.Visitors);
        q.Metrics.AddRange(revenue);
        q.OrderBy.Add(Order.By(Metric.Sum("revenue")));
      });
      Assert.Multiple(() =>
      {
        Assert.That(result.Totals.Values, Is.EqualTo(new double[] { 3, 2, 60, 20, 10, 30, 3 }), source.ToString());
        Assert.That(result.Rows.Select(x => (x["plan"], x[Metric.Sum("revenue")])), Is.EqualTo(new[] { ("pro", 50.0), ("basic", 10.0) }));
      });
    }
    var heartbeat = Query(q => q.Metrics.Add(Metric.Average("cpu", "heartbeat")));
    Assert.Multiple(() =>
    {
      Assert.That(heartbeat.Totals[Metric.Average("cpu", "heartbeat")], Is.EqualTo(0.5));
      Assert.That(engine.GetEventNames(Day(Day0)), Is.EqualTo(new[] { "$session", "heartbeat", "purchase" }));
    });
  }

  [Test]
  public void TrackValidatesAndCopiesEvents()
  {
    Assert.Throws<ArgumentException>(() => engine.Track(new AnalyticsEvent("")));
    Assert.Throws<ArgumentException>(() => engine.Track(new AnalyticsEvent("$session")));
    Assert.Throws<ArgumentNullException>(() => engine.Track(null));

    var reused = new AnalyticsEvent("purchase") { VisitorId = "1", Timestamp = Utc(T0) }.WithMetric("revenue", 10);
    engine.Track(reused);
    reused.Name = "refund";
    reused.Metrics["revenue"] = 99;
    engine.Track(PageView("/", "10.0.0.9", UserAgents.Googlebot, T0));
    engine.Track(new AnalyticsEvent("future") { Timestamp = DateTimeOffset.UtcNow.AddDays(2) });
    engine.Flush();
    var statistics = engine.GetStatistics();
    var purchase = Query(q =>
    {
      q.Event = "purchase";
      q.Metrics.Add(Metric.Sum("revenue"));
    });
    Assert.Multiple(() =>
    {
      Assert.That(purchase.Totals[Metric.Sum("revenue")], Is.EqualTo(10));
      Assert.That(statistics.BotsExcluded, Is.EqualTo(1));
      Assert.That(statistics.EventsRejected, Is.EqualTo(1));
      Assert.That(statistics.EventsProcessed, Is.EqualTo(1));
    });
  }

  [Test]
  public void EventFilterRunsOnTheCallingThread()
  {
    var callingThread = Environment.CurrentManagedThreadId;
    var filterThreads = new HashSet<int>();
    Recreate(o => o.EventFilter = e =>
    {
      lock (filterThreads)
        filterThreads.Add(Environment.CurrentManagedThreadId);
      return e.IpAddress != "10.0.0.2";
    });
    TrackScenario();
    engine.Flush();
    Assert.Multiple(() =>
    {
      Assert.That(filterThreads, Is.EqualTo(new[] { callingThread }));
      Assert.That(engine.GetStatistics().EventsFiltered, Is.EqualTo(1));
      Assert.That(Query(_ => { }).Totals[Metric.Events], Is.EqualTo(5));
    });
  }

  [Test]
  public void SitesAreIsolated()
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Site = "a.com", Url = "/", VisitorId = "1", Timestamp = Utc(T0) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Site = "a.com", Url = "/x", VisitorId = "2", Timestamp = Utc(T0) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Site = "b.com", Url = "/", VisitorId = "1", Timestamp = Utc(T0) });
    engine.Flush();
    foreach (var source in Sources)
    {
      double Count(string site) => Query(q =>
      {
        q.Site = site;
        q.Source = source;
      }).Totals[Metric.Events];
      Assert.Multiple(() =>
      {
        Assert.That(Count("a.com"), Is.EqualTo(2));
        Assert.That(Count("b.com"), Is.EqualTo(1));
        Assert.That(Count(null), Is.EqualTo(0));
      });
    }
  }

  [Test]
  public void DataSurvivesRestartAndSessionsContinue()
  {
    TrackScenario();
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "alice", Timestamp = Utc(T0) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/a", VisitorId = "alice", Timestamp = Utc(T0.AddMinutes(3)) });
    engine.Dispose();
    engine = TestEngine.Create(directory);
    var totals = Query(q => q.Metrics.AddRange([Metric.PageViews, Metric.Sessions])).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(totals[Metric.PageViews], Is.EqualTo(8));
      Assert.That(totals[Metric.Sessions], Is.EqualTo(5));
    });

    // Alice continues the same session after the restart. Anonymous visitors of past days
    // cannot continue: their daily salt is destroyed by design.
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/b", VisitorId = "alice", Timestamp = Utc(T0.AddMinutes(5)) });
    engine.Flush();
    var alice = Query(q =>
    {
      // Alice has web context (a URL) but no user agent: her device is known to be empty.
      q.Filters.Add(Filter.Equal(StandardDimensions.Device, ""));
      q.Metrics.AddRange([Metric.Sessions, Metric.SessionDuration, Metric.PageViewsPerSession, Metric.Bounces]);
    }).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(alice[Metric.Sessions], Is.EqualTo(1));
      Assert.That(alice[Metric.SessionDuration], Is.EqualTo(300).Within(1e-6));
      Assert.That(alice[Metric.PageViewsPerSession], Is.EqualTo(3));
      Assert.That(alice[Metric.Bounces], Is.EqualTo(0));
      Assert.That(engine.GetStatistics().SessionsStarted, Is.EqualTo(0));
    });
  }

  [Test]
  public void StorageFormatSettingsCannotChange()
  {
    engine.Dispose();
    engine = null;
    Assert.Throws<AnalyticsConfigurationException>(() =>
        TestEngine.Create(directory, options => options.Rollups.HyperLogLogPrecision = 12).Dispose());
    engine = TestEngine.Create(directory);
  }

  [Test]
  public void CurrentVisitorsAreRealTime()
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "1" });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "2" });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "2" });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "3", Timestamp = DateTimeOffset.UtcNow.AddHours(-1) });
    engine.Flush();
    Assert.That(engine.Web.GetCurrentVisitors(), Is.EqualTo(2));
  }

  [Test]
  public void ConcurrentIngestionWithSeveralWorkers()
  {
    Recreate(o => o.Ingestion.WorkerCount = 4);
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
          Timestamp = Utc(T0.AddSeconds(i % 3600))
        });
      }
    });
    engine.Flush();
    var totals = Query(q => q.Metrics.AddRange([Metric.PageViews, Metric.Visitors])).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(engine.GetStatistics().EventsProcessed, Is.EqualTo(threads * perThread));
      Assert.That(totals[Metric.PageViews], Is.EqualTo(threads * perThread));
      Assert.That(totals[Metric.Visitors], Is.EqualTo(1000));
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(0));
    });
  }

  [Test]
  public void RetentionDropsExpiredPartitions()
  {
    Recreate(o =>
    {
      o.Retention.RawEvents = TimeSpan.FromDays(30);
      o.Retention.HourRollups = TimeSpan.FromDays(60);
    });
    var old = DateTime.UtcNow.Date.AddDays(-200).AddHours(12);
    var recent = DateTime.UtcNow.Date.AddDays(-1).AddHours(12);
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(old) });
    engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(recent) });
    engine.Flush();
    Assert.That(engine.ApplyRetention(), Is.EqualTo(2), "raw and hourly partitions of the old month");

    var oldDay = engine.Query(new AnalyticsQuery { Event = "e", Range = Day(old.Date) });
    var oldRaw = engine.Query(new AnalyticsQuery { Event = "e", Range = Day(old.Date), Source = QuerySource.RawEvents });
    var recentHour = engine.Query(new AnalyticsQuery { Event = "e", Range = Range(recent.AddHours(-1), recent.AddHours(1)) });
    Assert.Multiple(() =>
    {
      Assert.That(oldDay.Totals[Metric.Events], Is.EqualTo(1));
      Assert.That(oldDay.Plan.UsedRawEvents, Is.False);
      Assert.That(oldRaw.Totals[Metric.Events], Is.EqualTo(0));
      Assert.That(oldRaw.Plan.IsComplete, Is.False);
      Assert.That(oldRaw.Plan.Warnings.Single(), Does.Contain("retention"));
      Assert.That(recentHour.Totals[Metric.Events], Is.EqualTo(1));
    });
  }

  [Test]
  public void RawEventsNeverContainIpOrUserAgent()
  {
    TrackScenario();
    engine.Dispose();
    engine = null;
    var events = new List<RawEvent>();
    var options = TestEngine.Options(directory);
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
        events.Add(e);
        return true;
      });
    }
    var values = events.SelectMany(x => x.Dimensions.Select(d => d.Value)).ToList();
    Assert.Multiple(() =>
    {
      Assert.That(events, Has.Count.EqualTo(6));
      Assert.That(values, Has.None.Contains("10.0.0."));
      Assert.That(values, Has.None.Contains("Mozilla"));
      Assert.That(events.Select(x => x.Visitor).Distinct().Count(), Is.EqualTo(3));
    });
    engine = TestEngine.Create(directory);
  }

  [Test]
  public async Task EraseVisitorRemovesRawEvents()
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "alice", Timestamp = Utc(T0) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/a", VisitorId = "alice", Timestamp = Utc(T0.AddMinutes(1)) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "bob", Timestamp = Utc(T0) });
    var deleted = await engine.EraseVisitorAsync("alice");
    Assert.Multiple(() =>
    {
      Assert.That(deleted, Is.EqualTo(2));
      Assert.That(Query(q => q.Source = QuerySource.RawEvents).Totals[Metric.Events], Is.EqualTo(1));
    });
  }

  [Test]
  public void ExplicitOnlyIdentificationIgnoresFingerprints()
  {
    Recreate(o => o.Privacy.VisitorIdentification = VisitorIdentification.ExplicitOnly);
    TrackScenario();
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "u", Timestamp = Utc(T0) });
    engine.Flush();
    var totals = Query(_ => { }).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(totals[Metric.Events], Is.EqualTo(7));
      Assert.That(totals[Metric.Visitors], Is.EqualTo(1));
    });
  }

  [Test]
  public void QueriesWithoutRollupAndWithoutRawEventsFail()
  {
    Recreate(o => o.Storage.StoreRawEvents = false);
    Assert.Throws<AnalyticsQueryException>(() => Query(q => q.GroupBy.AddRange([StandardDimensions.Page, StandardDimensions.Country])));
    Assert.Throws<AnalyticsQueryException>(() => Query(q => q.Segments.Add(Segment.VisitorsWhoViewed("/"))));
  }

  [Test]
  public void QueriesCanBeCancelled()
  {
    TrackScenario();
    engine.Flush();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Assert.That(
        async () => await engine.QueryAsync(new AnalyticsQuery { Range = Day(Day0) }, cancellation.Token),
        Throws.InstanceOf<OperationCanceledException>());
  }
}
