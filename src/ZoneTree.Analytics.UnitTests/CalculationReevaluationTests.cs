using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Query;

namespace ZoneTree.Analytics.UnitTests;

// Regression cases and independent oracles from the second calculation review.
public sealed class CalculationReevaluationTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-8);

  [Test]
  public void RawDistinctSessionDimensionsAgreeWithRollups()
  {
    using var engine = TestEngine.Create(directory, o => o.Rollups.DistinctDimensions.Add("country"));
    foreach (var country in new[] { "DE", "US" })
      engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = country, Timestamp = Utc(Day0.AddHours(10)), Dimensions = { ["country"] = country }
      });
    engine.Flush();
    double Read(QuerySource source) => engine.Query(new AnalyticsQuery
    {
      Event = StandardEvents.Session, Range = Day(Day0), Source = source,
      Metrics = { Metric.Distinct("country") }
    }).Totals[Metric.Distinct("country")];
    Assert.That(Read(QuerySource.Rollups), Is.EqualTo(2));
    Assert.That(Read(QuerySource.RawEvents), Is.EqualTo(2));
  }

  [TestCase(1)]
  [TestCase(4)]
  public void SessionMetricsIncludeAssignedLateEventsBeforeTheQueryRange(int parallelism)
  {
    using var engine = TestEngine.Create(directory, o => o.Queries.MaxDegreeOfParallelism = parallelism);
    // The session starts today, then receives a page view with an earlier timestamp.
    engine.Track(new AnalyticsEvent("pageview")
    {
      VisitorId = "alice", Url = "/entry", Timestamp = Utc(Day0.AddMinutes(5))
    });
    engine.Track(new AnalyticsEvent("pageview")
    {
      VisitorId = "alice", Url = "/late", Timestamp = Utc(Day0.AddMinutes(-5))
    });
    engine.Flush();
    AnalyticsRow Read(QuerySource source) => engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Source = source,
      Metrics = { Metric.Sessions, Metric.Bounces, Metric.PageViewsPerSession }
    }).Totals;
    var rollups = Read(QuerySource.Rollups);
    var raw = Read(QuerySource.RawEvents);
    Assert.Multiple(() =>
    {
      Assert.That(rollups[Metric.Sessions], Is.EqualTo(1));
      Assert.That(rollups[Metric.Bounces], Is.Zero);
      Assert.That(rollups[Metric.PageViewsPerSession], Is.EqualTo(2));
      Assert.That(raw[Metric.Sessions], Is.EqualTo(rollups[Metric.Sessions]));
      Assert.That(raw[Metric.Bounces], Is.EqualTo(rollups[Metric.Bounces]));
      Assert.That(raw[Metric.PageViewsPerSession], Is.EqualTo(rollups[Metric.PageViewsPerSession]));
    });
  }

  [Test]
  public void MovingAverageOfLargeConstantGaugesStaysFinite()
  {
    using var engine = TestEngine.Create(directory);
    for (var i = 0; i < 3; ++i)
      engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(Day0.AddDays(i)) }.WithValue("x", 1e308));
    engine.Flush();
    var average = Metric.MovingAverage(Metric.Last("x"), 2);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "gauge", Range = Range(Day0, Day0.AddDays(3)), Interval = TimeInterval.Day,
      Metrics = { average }
    });
    Assert.That(result.Rows.Select(r => r[average]), Is.EqualTo(new double[] { 1e308, 1e308, 1e308 }));
  }

  [Test]
  public void LinearInterpolationOfOppositeLargeGaugesStaysFinite()
  {
    using var engine = TestEngine.Create(directory);
    engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(Day0) }.WithValue("x", -1e308));
    engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(Day0.AddDays(2)) }.WithValue("x", 1e308));
    engine.Flush();
    var last = Metric.Last("x");
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "gauge", Range = Range(Day0, Day0.AddDays(3)), Interval = TimeInterval.Day,
      Fill = FillMode.Linear, Metrics = { last }
    });
    Assert.That(result.Rows[1][last], Is.Zero);
  }

  [Test]
  public void LagOnSparseSeriesUsesTheRequestedCalendarBucket()
  {
    using var engine = TestEngine.Create(directory);
    engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(Day0) }.WithValue("x", 10));
    engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(Day0.AddDays(2)) }.WithValue("x", 20));
    engine.Flush();
    var lag = Metric.Lag(Metric.Last("x"), 1);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "gauge", Range = Range(Day0, Day0.AddDays(3)), Interval = TimeInterval.Day,
      Fill = FillMode.None, Metrics = { lag }
    });
    Assert.That(result.Rows[1][lag], Is.Zero, "The immediately preceding day has no bucket value.");
  }

  [Test]
  public void MovingAverageRecoversAfterAnExtremeObservationLeavesTheWindow()
  {
    using var engine = TestEngine.Create(directory);
    double[] values = [1e16, 1, 1, 1];
    for (var i = 0; i < values.Length; ++i)
      engine.Track(new AnalyticsEvent("gauge") { Timestamp = Utc(Day0.AddDays(i)) }.WithValue("x", values[i]));
    engine.Flush();
    var average = Metric.MovingAverage(Metric.Last("x"), 2);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "gauge", Range = Range(Day0, Day0.AddDays(values.Length)), Interval = TimeInterval.Day,
      Metrics = { average }
    });
    Assert.That(result.Rows.Skip(2).Select(r => r[average]), Is.EqualTo(new double[] { 1, 1 }));
  }

  [Test]
  public void PlannerRandomizedCoverageStaysContinuousAndRespectsAvailability()
  {
    var random = new Random(99991);
    var calendar = new ReportingCalendar(TimeZoneInfo.Utc, DayOfWeek.Monday);
    for (var iteration = 0; iteration < 500; ++iteration)
    {
      var from = Day0.AddSeconds(random.Next(86400)).Ticks;
      var to = from + TimeSpan.FromSeconds(random.Next(1, 5 * 86400)).Ticks;
      RollupCoverage[] tiers =
      [
        new(Granularity.Day, Day0.AddSeconds(random.Next(4 * 86400)).Ticks),
        new(Granularity.Hour, Day0.AddSeconds(random.Next(4 * 86400)).Ticks),
        new(Granularity.Minute, Day0.AddSeconds(random.Next(4 * 86400)).Ticks)
      ];
      var plan = QueryRangePlanner.Build(calendar, from, to, tiers, true, default);
      var ordered = plan.Segments.OrderBy(s => s.From).ToArray();
      Assert.That(ordered[0].From, Is.EqualTo(from));
      Assert.That(ordered[^1].To, Is.EqualTo(to));
      for (var i = 1; i < ordered.Length; ++i)
        Assert.That(ordered[i].From, Is.EqualTo(ordered[i - 1].To));
      foreach (var segment in ordered.Where(s => s.Source != PlanSource.RawEvents))
        Assert.That(segment.From, Is.GreaterThanOrEqualTo(tiers.Single(t => t.Source == segment.Source).AvailableFrom));
      Assert.That(plan.HasMissingRanges, Is.False);
    }
  }

  [TestCase("none")]
  [TestCase("hour")]
  [TestCase("day")]
  public void OrderedSessionReportsMatchAnIndependentTrafficModel(string intervalName)
  {
    using var engine = TestEngine.Create(directory, o => o.Ingestion.WorkerCount = 3);
    var random = new Random(131071);
    var sessions = new List<(string Country, string Visitor, DateTime Start, int Views, int Minutes)>();
    for (var visitor = 0; visitor < 20; ++visitor)
    {
      for (var day = 0; day < 2; ++day)
      {
        var country = visitor % 2 == 0 ? "DE" : "US";
        var id = "v" + visitor;
        var start = Day0.AddDays(day).AddHours(visitor);
        var views = random.Next(1, 5);
        var minutes = (views - 1) * 3 + 2;
        sessions.Add((country, id, start, views, minutes));
        for (var page = 0; page < views; ++page)
          engine.Track(new AnalyticsEvent("pageview")
          {
            VisitorId = id, Url = "/page" + page, Timestamp = Utc(start.AddMinutes(page * 3)),
            Dimensions = { ["country"] = country }
          });
        engine.Track(new AnalyticsEvent("heartbeat")
        {
          VisitorId = id, NonInteractive = true, Timestamp = Utc(start.AddMinutes(minutes))
        });
      }
    }
    engine.Flush();
    var interval = TimeInterval.Parse(intervalName);
    DateTime Bucket(DateTime start) => intervalName switch
    {
      "hour" => start, "day" => start.Date, _ => DateTime.MinValue
    };
    Metric[] metrics =
    [
      Metric.PageViews, Metric.Visitors, Metric.Sessions, Metric.Bounces, Metric.BounceRate,
      Metric.SessionDuration, Metric.AverageSessionDuration, Metric.PageViewsPerSession,
      Metric.Entrances, Metric.Exits
    ];
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups, QuerySource.Auto })
    {
      var query = new AnalyticsQuery
      {
        Range = Range(Day0, Day0.AddDays(2)), Interval = interval, Fill = FillMode.None,
        GroupBy = { "country" }, Source = source
      };
      query.Metrics.AddRange(metrics);
      var result = engine.Query(query);
      foreach (var row in result.Rows)
      {
        var selected = sessions.Where(s => s.Country == row["country"] &&
            Bucket(s.Start) == (row.Bucket?.UtcDateTime ?? DateTime.MinValue)).ToArray();
        var bounces = selected.Count(s => s.Views == 1);
        Assert.Multiple(() =>
        {
          Assert.That(row[Metric.PageViews], Is.EqualTo(selected.Sum(s => s.Views)));
          Assert.That(row[Metric.Visitors], Is.EqualTo(selected.Select(s => s.Visitor).Distinct().Count()));
          Assert.That(row[Metric.Sessions], Is.EqualTo(selected.Length));
          Assert.That(row[Metric.Bounces], Is.EqualTo(bounces));
          Assert.That(row[Metric.BounceRate], Is.EqualTo((double)bounces / selected.Length));
          Assert.That(row[Metric.SessionDuration], Is.EqualTo(selected.Sum(s => s.Minutes) * 60));
          Assert.That(row[Metric.AverageSessionDuration], Is.EqualTo(selected.Average(s => s.Minutes) * 60).Within(1e-10));
          Assert.That(row[Metric.PageViewsPerSession], Is.EqualTo(selected.Average(s => s.Views)).Within(1e-10));
          Assert.That(row[Metric.Entrances], Is.EqualTo(selected.Length));
          Assert.That(row[Metric.Exits], Is.EqualTo(selected.Length));
        });
      }
      Assert.That(result.Rows.Count, Is.EqualTo(sessions.GroupBy(s => (s.Country, Bucket(s.Start))).Count()));
      Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(20));
      Assert.That(result.Totals[Metric.Sessions], Is.EqualTo(40));
    }
  }
}
