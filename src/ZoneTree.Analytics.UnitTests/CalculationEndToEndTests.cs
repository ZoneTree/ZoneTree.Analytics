using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class CalculationEndToEndTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-8);

  [TestCase(1, 30)]
  [TestCase(4, 30)]
  [TestCase(1, 31)]
  [TestCase(4, 31)]
  public void SessionLookbackHonorsTheAssignmentDeadlineAndSessionIdentity(int parallelism, int minutes)
  {
    using var engine = TestEngine.Create(directory, o => o.Queries.MaxDegreeOfParallelism = parallelism);
    void View(string visitor, DateTime time, string page) => engine.Track(new AnalyticsEvent("pageview")
    {
      VisitorId = visitor, Timestamp = Utc(time), Url = page
    });
    View("alice", Day0.AddMinutes(5), "/entry");
    View("alice", Day0.AddMinutes(5 - minutes), "/late");
    View("bob", Day0.AddMinutes(-10), "/unrelated");
    engine.Flush();
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups, QuerySource.Auto })
    {
      var result = engine.Query(new AnalyticsQuery
      {
        Range = Day(Day0), Source = source,
        Metrics = { Metric.PageViews, Metric.Visitors, Metric.Sessions, Metric.Bounces, Metric.PageViewsPerSession }
      });
      Assert.Multiple(() =>
      {
        Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(1));
        Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(1));
        Assert.That(result.Totals[Metric.Sessions], Is.EqualTo(1));
        Assert.That(result.Totals[Metric.Bounces], Is.EqualTo(minutes == 30 ? 0 : 1));
        Assert.That(result.Totals[Metric.PageViewsPerSession], Is.EqualTo(minutes == 30 ? 2 : 1));
        Assert.That(result.Plan.IsComplete, Is.True);
      });
    }
  }

  [TestCase(1)]
  [TestCase(4)]
  public void SegmentedSessionReplayCompletesBothDirectionsAfterCompactionAndReopen(int parallelism)
  {
    void Configure(AnalyticsOptions options) => options.Queries.MaxDegreeOfParallelism = parallelism;
    using (var engine = TestEngine.Create(directory, Configure))
    {
      void Track(string visitor, string name, string page, int minute, bool nonInteractive = false) =>
          engine.Track(new AnalyticsEvent(name)
          {
            VisitorId = visitor, Url = page, Timestamp = Utc(Day0.AddMinutes(minute)),
            NonInteractive = nonInteractive, Dimensions = { ["country"] = "DE" }
          });
      Track("alice", "pageview", "/entry", 5);
      Track("alice", "pageview", "/late", -10);
      Track("alice", "heartbeat", null, 25, true);
      Track("bob", "pageview", "/other", 6);
      engine.Compact();
    }
    using var reopened = TestEngine.Create(directory, Configure);
    var result = reopened.Query(new AnalyticsQuery
    {
      Range = Range(Day0, Day0.AddMinutes(10)), Source = QuerySource.Auto,
      Segments = { Segment.SessionsWith("pageview", Filter.Equal("page", "/entry")) },
      Metrics =
      {
        Metric.PageViews, Metric.Visitors, Metric.Sessions, Metric.Bounces, Metric.PageViewsPerSession,
        Metric.SessionDuration, Metric.Entrances, Metric.Exits
      }
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(1));
      Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(1));
      Assert.That(result.Totals[Metric.Sessions], Is.EqualTo(1));
      Assert.That(result.Totals[Metric.Bounces], Is.Zero);
      Assert.That(result.Totals[Metric.PageViewsPerSession], Is.EqualTo(2));
      Assert.That(result.Totals[Metric.SessionDuration], Is.EqualTo(20 * 60));
      Assert.That(result.Totals[Metric.Entrances], Is.EqualTo(1));
      Assert.That(result.Totals[Metric.Exits], Is.EqualTo(1));
      Assert.That(result.Plan.IsComplete, Is.True);
    });
    var report = reopened.Web.GetSummary(new WebReportRequest
    {
      Range = Range(Day0, Day0.AddMinutes(10)),
      Segments = { Segment.SessionsWith("pageview", Filter.Equal("page", "/entry")) }
    });
    Assert.That(report.Summary.BounceRate, Is.Zero);
    Assert.That(report.Summary.PageViewsPerSession, Is.EqualTo(2));
  }

  [Test]
  public void ExpiredSessionLookbackWarnsWithoutMakingOrdinaryEventQueriesIncomplete()
  {
    var cutoff = DateTime.UtcNow.Date.AddDays(-2);
    using var engine = TestEngine.Create(directory, o => o.Retention.RawEvents = TimeSpan.FromDays(2));
    engine.Track(new AnalyticsEvent("pageview")
    {
      VisitorId = "alice", Timestamp = Utc(cutoff.AddMinutes(5)), Url = "/entry"
    });
    engine.Track(new AnalyticsEvent("pageview")
    {
      VisitorId = "alice", Timestamp = Utc(cutoff.AddMinutes(-5)), Url = "/late"
    });
    engine.Flush();
    var sessions = engine.Query(new AnalyticsQuery
    {
      Range = Day(cutoff), Source = QuerySource.RawEvents, Metrics = { Metric.Sessions, Metric.Bounces }
    });
    var events = engine.Query(new AnalyticsQuery
    {
      Range = Day(cutoff), Source = QuerySource.RawEvents, Metrics = { Metric.PageViews }
    });
    var identities = engine.Query(new AnalyticsQuery
    {
      Event = StandardEvents.Session, Range = Day(cutoff), Source = QuerySource.RawEvents,
      Metrics = { Metric.Events, Metric.Visitors, Metric.Distinct("entry_page") }
    });
    Assert.Multiple(() =>
    {
      Assert.That(sessions.Plan.IsComplete, Is.False);
      Assert.That(sessions.Plan.Warnings.Any(w => w.Contains("outside retention", StringComparison.Ordinal)), Is.True);
      Assert.That(events.Plan.IsComplete, Is.True);
      Assert.That(events.Totals[Metric.PageViews], Is.EqualTo(1));
      Assert.That(identities.Plan.IsComplete, Is.True);
      Assert.That(identities.Totals[Metric.Events], Is.EqualTo(1));
      Assert.That(identities.Totals[Metric.Distinct("entry_page")], Is.EqualTo(1));
    });
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  [TestCase(QuerySource.Auto)]
  public void SessionDistinctCountsSupportCustomDimensionsConditionsAndMissingValues(QuerySource source)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Sessions.Dimensions.Add("plan");
      o.Rollups.Definitions.Add(new RollupDefinition("plan"));
      o.Rollups.DistinctDimensions.AddRange(["plan", "country", "entry_page"]);
    });
    foreach (var (id, country, plan, page) in new[]
    {
      ("a", "DE", "pro", "/a"), ("b", "DE", "basic", "/b"),
      ("c", "US", "pro", "/a"), ("d", (string)null, "", "/c"), ("e", "DE", (string)null, "/d")
    })
      engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = id, Site = "one", Url = page, Timestamp = Utc(Day0.AddHours(10)),
        Dimensions = { ["country"] = country, ["plan"] = plan }
      });
    engine.Track(new AnalyticsEvent("pageview")
    {
      VisitorId = "other", Site = "two", Url = "/other", Timestamp = Utc(Day0.AddHours(10)),
      Dimensions = { ["country"] = "FR", ["plan"] = "other" }
    });
    engine.Flush();
    var plans = Metric.Distinct("plan");
    var countries = Metric.Distinct("country");
    var entries = Metric.Distinct("entry_page");
    var germanPlans = plans.Where(Filter.Equal("country", "DE"));
    var result = engine.Query(new AnalyticsQuery
    {
      Site = "one", Event = StandardEvents.Session, Range = Day(Day0), Source = source,
      GroupBy = { "country" }, Metrics = { Metric.Events, plans, countries, entries, germanPlans }
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(5));
      Assert.That(result.Totals[plans], Is.EqualTo(3));
      Assert.That(result.Totals[countries], Is.EqualTo(3)); // Unknown geolocation is a known empty country.
      Assert.That(result.Totals[entries], Is.EqualTo(4));
      Assert.That(result.Totals[germanPlans], Is.EqualTo(2));
      Assert.That(result.Rows.Single(r => r["country"] == "DE")[germanPlans], Is.EqualTo(2));
      Assert.That(result.Rows.Single(r => r["country"] == "")[countries], Is.EqualTo(1));
    });
    var byPlan = engine.Query(new AnalyticsQuery
    {
      Site = "one", Event = StandardEvents.Session, Range = Day(Day0), Source = source,
      GroupBy = { "plan" }, Metrics = { plans }
    });
    Assert.That(byPlan.Rows.Single(r => r["plan"] == null)[plans], Is.Zero);
    Assert.That(byPlan.Rows.Single(r => r["plan"] == "")[plans], Is.EqualTo(1));
  }

  [Test]
  public void UnsupportedSyntheticSessionBreakdownsAreRejected()
  {
    using var engine = TestEngine.Create(directory);
    Assert.Throws<AnalyticsQueryException>(() => engine.Query(new AnalyticsQuery
    {
      Event = StandardEvents.Session, Range = Day(Day0), GroupBy = { "page" }, Metrics = { Metric.Events }
    }));
    Assert.Throws<AnalyticsQueryException>(() => engine.Query(new AnalyticsQuery
    {
      Event = StandardEvents.Session, Range = Day(Day0), Metrics = { Metric.Distinct("page") }
    }));
  }

  [TestCase("day")]
  [TestCase("week")]
  [TestCase("month")]
  [TestCase("year")]
  [TestCase("5m")]
  [TestCase("6h")]
  public void SparseShiftsUseIntervalPositionsAcrossGroups(string intervalName)
  {
    var interval = TimeInterval.Parse(intervalName);
    var start = new DateTime(2023, 1, 2);
    var bucket1 = ReportingCalendar.NextBucket(start.Ticks, interval);
    var bucket2 = ReportingCalendar.NextBucket(bucket1, interval);
    var bucket3 = ReportingCalendar.NextBucket(bucket2, interval);
    using var engine = TestEngine.Create(directory, o => o.Rollups.Definitions.Add(new RollupDefinition("group")));
    foreach (var (group, value) in new[] { ("a", 10.0), ("b", 100.0) })
    {
      engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(start) }.WithDimension("group", group).WithValue("x", value));
      engine.Track(new AnalyticsEvent("gauge") { Timestamp = new DateTimeOffset(bucket2, TimeSpan.Zero) }
          .WithDimension("group", group).WithValue("x", value * 2));
    }
    engine.Flush();
    var lag = Metric.Lag(Metric.Last("x"));
    var lead = Metric.Lead(Metric.Last("x"));
    var twoBack = Metric.Lag(Metric.Last("x"), 2);
    var twoAhead = Metric.Lead(Metric.Last("x"), 2);
    var query = new AnalyticsQuery
    {
      Event = "gauge", Range = DateRange.Between(Utc(start), new DateTimeOffset(bucket3, TimeSpan.Zero)),
      Interval = interval, Fill = FillMode.None, GroupBy = { "group" },
      Metrics = { lag, lead, twoBack, twoAhead }
    };
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Auto })
    {
      query.Source = source;
      var result = engine.Query(query);
      foreach (var group in new[] { "a", "b" })
      {
        var rows = result.Rows.Where(r => r["group"] == group).ToArray();
        var value = group == "a" ? 10 : 100;
        Assert.Multiple(() =>
        {
          Assert.That(rows.Select(r => r[lag]), Is.All.Zero);
          Assert.That(rows.Select(r => r[lead]), Is.All.Zero);
          Assert.That(rows[0][twoAhead], Is.EqualTo(value * 2));
          Assert.That(rows[1][twoBack], Is.EqualTo(value));
        });
      }
    }
  }

  [Test]
  public void SparseDayShiftsUseCalendarDaysAcrossDst()
  {
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    var calendar = new ReportingCalendar(zone, DayOfWeek.Monday);
    var start = new DateTime(2026, 3, 7);
    using var engine = TestEngine.Create(directory, o => o.TimeZone = zone);
    foreach (var day in new[] { 0, 2 })
      engine.Track(new AnalyticsEvent("gauge")
      {
        Timestamp = new DateTimeOffset(calendar.UtcStartOfLocalDay(start.AddDays(day).Ticks), TimeSpan.Zero)
      }.WithValue("x", day == 0 ? 10 : 20));
    engine.Flush();
    var lag = Metric.Lag(Metric.Last("x"), 2);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "gauge", Range = DateRange.Days(DateOnly.FromDateTime(start), DateOnly.FromDateTime(start.AddDays(2))),
      Interval = TimeInterval.Day, Fill = FillMode.None, Metrics = { lag }
    });
    Assert.That(result.Rows[1][lag], Is.EqualTo(10));
  }

  [Test]
  public void SparseMovingAveragesMatchZeroFilledRowsAtTheSameTimes()
  {
    using var engine = TestEngine.Create(directory);
    foreach (var (day, value) in new[] { (2, 30.0), (4, 60.0), (8, 90.0) })
      engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(Day0.AddDays(day)) }.WithValue("x", value));
    engine.Flush();
    var average = Metric.MovingAverage(Metric.Last("x"), 3);
    AnalyticsResult Read(FillMode fill) => engine.Query(new AnalyticsQuery
    {
      Event = "gauge", Range = Range(Day0, Day0.AddDays(9)), Interval = TimeInterval.Day,
      Fill = fill, Metrics = { average }
    });
    var filled = Read(FillMode.Zero);
    var sparse = Read(FillMode.None);
    Assert.That(sparse.Rows.Select(r => r[average]), Is.EqualTo(new double[] { 10, 30, 30 }));
    foreach (var row in sparse.Rows)
      Assert.That(row[average], Is.EqualTo(filled.Rows.Single(r => r.Bucket == row.Bucket)[average]));
  }

  [Test]
  public void MovingAveragesMatchDecimalArithmeticAfterSignedOutliersAndCancellation()
  {
    using var engine = TestEngine.Create(directory);
    var random = new Random(524287);
    var values = Enumerable.Range(0, 120).Select(i =>
        i % 11 == 0 ? (i % 2 == 0 ? 1e16 : -1e16) : random.Next(-20, 21)).ToArray();
    for (var i = 0; i < values.Length; ++i)
      engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(Day0.AddMinutes(i)) }.WithValue("x", values[i]));
    engine.Flush();
    foreach (var window in new[] { 2, 3, 7, 31 })
    {
      var average = Metric.MovingAverage(Metric.Last("x"), window);
      var result = engine.Query(new AnalyticsQuery
      {
        Event = "gauge", Range = Range(Day0, Day0.AddMinutes(values.Length)), Interval = TimeInterval.Minute,
        Metrics = { average }
      });
      for (var i = 0; i < values.Length; ++i)
      {
        var first = Math.Max(0, i - window + 1);
        var expected = (double)(values.Skip(first).Take(i - first + 1).Sum(v => (decimal)v) / (i - first + 1));
        Assert.That(result.Rows[i][average], Is.EqualTo(expected).Within(Math.Max(1e-12, Math.Abs(expected) * 3e-16)),
            $"window {window}, bucket {i}");
      }
    }
  }

  [Test]
  public void MovingAverageSupportsTheMaximumWindow()
  {
    using var engine = TestEngine.Create(directory);
    const int count = 10005;
    var start = new DateTime(2025, 1, 1);
    for (var i = 0; i < count; ++i)
      engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(start.AddMinutes(i)) }.WithValue("x", i % 13 - 6));
    engine.Flush();
    var average = Metric.MovingAverage(Metric.Last("x"), 10000);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "gauge", Range = Range(start, start.AddMinutes(count)), Interval = TimeInterval.Minute,
      Metrics = { average }
    });
    Assert.That(result.Rows.Count, Is.EqualTo(count));
    foreach (var i in new[] { 0, 1, 9999, 10000, 10004 })
    {
      var first = Math.Max(0, i - 9999);
      var expected = Enumerable.Range(first, i - first + 1).Sum(v => v % 13 - 6) / (double)(i - first + 1);
      Assert.That(result.Rows[i][average], Is.EqualTo(expected).Within(1e-14));
    }
  }
}
