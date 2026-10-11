using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Query;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

// Regression cases and independent arithmetic checks from the pre-publication audit.
public sealed class CalculationCorrectnessAuditTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-4);

  [Test]
  public void DailyBucketsInHalfHourTimeZoneAgreeWithRawEvents()
  {
    using var engine = TestEngine.Create(directory);
    foreach (var minute in new[] { 15, 45 })
      engine.Track(new AnalyticsEvent("sample") { Timestamp = Utc(Day0.AddHours(18).AddMinutes(minute)) });
    engine.Flush();
    var zone = TimeZoneInfo.CreateCustomTimeZone("Audit UTC+05:30", TimeSpan.FromMinutes(330), "Audit", "Audit");
    AnalyticsResult Read(QuerySource source) => engine.Query(new AnalyticsQuery
    {
      Event = "sample", Range = Range(Day0, Day0.AddDays(2)), TimeZone = zone,
      Interval = TimeInterval.Day, Source = source, Metrics = { Metric.Events }
    });
    var raw = Read(QuerySource.RawEvents);
    var auto = Read(QuerySource.Auto);
    Assert.That(raw.Rows.Select(r => r[Metric.Events]), Is.EqualTo(new double[] { 1, 1, 0 }));
    Assert.That(auto.Rows.Select(r => r[Metric.Events]), Is.EqualTo(raw.Rows.Select(r => r[Metric.Events])));
  }

  [Test]
  public void RepeatedFirstStepPreservesValidFunnelCompletion()
  {
    using var engine = TestEngine.Create(directory);
    foreach (var (name, minute) in new[] { ("a", 0), ("a", 5), ("b", 6), ("c", 11) })
      engine.Track(new AnalyticsEvent(name) { VisitorId = "alice", Timestamp = Utc(Day0.AddHours(10).AddMinutes(minute)) });
    engine.Flush();
    var result = engine.Query(new FunnelQuery
    {
      Range = Day(Day0), Window = TimeSpan.FromMinutes(10),
      Steps = { FunnelStep.Goal("a"), FunnelStep.Goal("b"), FunnelStep.Goal("c") }
    });
    // a@5 -> b@6 -> c@11 completes in six minutes.
    Assert.That(result.Steps.Select(s => s.Visitors), Is.EqualTo(new long[] { 1, 1, 1 }));
  }

  [Test]
  public void EarlierLatePageViewDoesNotRemoveRawEntrance()
  {
    using var engine = TestEngine.Create(directory);
    foreach (var (page, minute) in new[] { ("/entry", 10), ("/late", 5), ("/exit", 15) })
      engine.Track(new AnalyticsEvent(StandardEvents.PageView)
      {
        VisitorId = "alice", Url = page, Timestamp = Utc(Day0.AddHours(10).AddMinutes(minute))
      });
    engine.Flush();
    double Read(QuerySource source) => engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Source = source, Metrics = { Metric.Entrances }
    }).Totals[Metric.Entrances];
    Assert.That(Read(QuerySource.Rollups), Is.EqualTo(1));
    Assert.That(Read(QuerySource.RawEvents), Is.EqualTo(1));
  }

  [Test]
  public void PlannerUsesAvailableRollupsAfterTheirRetentionBoundary()
  {
    var calendar = new ReportingCalendar(TimeZoneInfo.Utc, DayOfWeek.Monday);
    var from = Day0.AddDays(-200).Ticks;
    var available = Day0.AddDays(-100).Ticks;
    var to = Day0.Ticks;
    var plan = QueryRangePlanner.Build(calendar, from, to,
        new[] { new RollupCoverage(Granularity.Hour, available) }, true, CancellationToken.None);
    Assert.That(plan.Segments.Any(s => s.Source == PlanSource.HourRollups && s.From >= available), Is.True,
        "Retained rollups must cover the recent half of the range even when its start is older than retention.");
  }

  [Test]
  public void AutoQueryKeepsRetainedRollupDataWhenRangeStartsBeforeRetention()
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Rollups.Granularities = Granularity.Hour;
      o.Retention.HourRollups = TimeSpan.FromDays(100);
      o.Retention.RawEvents = TimeSpan.FromDays(10);
    });
    engine.Track(new AnalyticsEvent("sample") { Timestamp = Utc(Day0.AddDays(-50).AddHours(12)) });
    engine.Flush();
    double Read(QuerySource source) => engine.Query(new AnalyticsQuery
    {
      Event = "sample", Range = Range(Day0.AddDays(-200), Day0), Source = source,
      Metrics = { Metric.Events }
    }).Totals[Metric.Events];
    Assert.That(Read(QuerySource.Rollups), Is.EqualTo(1));
    Assert.That(Read(QuerySource.Auto), Is.EqualTo(1));
  }

  [Test]
  public void BaseEventRowRestrictionWorksWithoutSelectingBaseMetric()
  {
    using var engine = TestEngine.Create(directory);
    engine.Track(new AnalyticsEvent("signup") { VisitorId = "alice", Timestamp = Utc(Day0) });
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), OnlyRowsWithBaseEvent = true, Metrics = { Metric.EventsOf("signup") }
    });
    Assert.That(result.Rows, Is.Empty, "No page views exist, so no row satisfies the base-event restriction.");
  }

  [Test]
  public async Task IdleWorkerFlushesWithinConfiguredInterval()
  {
    using var engine = TestEngine.Create(directory, o => o.Ingestion.FlushInterval = TimeSpan.FromMilliseconds(500));
    engine.Track(new AnalyticsEvent("sample") { Timestamp = Utc(Day0) });
    Assert.That(SpinWait.SpinUntil(() => engine.GetStatistics().EventsProcessed == 1, 3000), Is.True);
    await Task.Delay(1000);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "sample", Range = Day(Day0), Metrics = { Metric.Events }
    });
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
  }

  [Test]
  public void ConstantLargeValuesHaveZeroVarianceAndFiniteAverage()
  {
    var stats = ValueStats.Combine(ValueStats.Of(1e308), ValueStats.Of(1e308));
    stats = ValueStats.Combine(stats, ValueStats.Of(1e308));
    Assert.Multiple(() =>
    {
      Assert.That(stats.Average, Is.EqualTo(1e308));
      Assert.That(stats.Variance, Is.Zero);
    });
  }

  [Test]
  public void ConstantValuesWithoutOverflowHaveZeroVariance()
  {
    var stats = ValueStats.Of(1e16);
    for (var i = 1; i < 10000; ++i)
      stats = ValueStats.Combine(stats, ValueStats.Of(1e16));
    Assert.That(stats.Variance, Is.Zero);
  }

  [Test]
  public void QuantileRepresentativeForLargestFiniteValuesStaysFinite()
  {
    var sketch = new QuantileSketch();
    sketch.Add(1e308);
    sketch.Add(double.MaxValue);
    Assert.That(sketch.Quantile(0.5), Is.EqualTo(1e308).Within(1e306));
  }

  [Test]
  public void PublicPercentileOfLargeFiniteValuesMeetsAccuracy()
  {
    using var engine = TestEngine.Create(directory);
    foreach (var value in new[] { 1e308, double.MaxValue })
      engine.Track(new AnalyticsEvent("sample") { Timestamp = Utc(Day0) }.WithValue("x", value));
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "sample", Range = Day(Day0), Source = QuerySource.RawEvents, Metrics = { Metric.Median("x") }
    });
    Assert.That(result.Totals[Metric.Median("x")], Is.EqualTo(1e308).Within(1e306));
  }

  [Test]
  public void RandomOrdinaryAggregatesMatchIndependentArithmetic()
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Rollups.Definitions.Add(new RollupDefinition("group"));
      o.Rollups.ValueAggregates["x"] = ValueAggregates.All;
      o.Rollups.DistinctDimensions.Add("identity");
      o.Ingestion.WorkerCount = 3;
    });
    var random = new Random(104729);
    var events = Enumerable.Range(0, 300).Select(i => new
    {
      Group = "g" + random.Next(4), Value = (double)random.Next(-1000, 1000),
      Visitor = "v" + random.Next(30), Identity = "d" + random.Next(17),
      Time = Day0.AddMinutes(i * 13)
    }).ToArray();
    foreach (var e in events)
      engine.Track(new AnalyticsEvent("sample") { VisitorId = e.Visitor, Timestamp = Utc(e.Time) }
          .WithDimension("group", e.Group).WithDimension("identity", e.Identity).WithValue("x", e.Value));
    engine.Flush();
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups, QuerySource.Auto })
    {
      var start = source == QuerySource.Rollups ? Day0 : Day0.AddMinutes(1);
      var end = source == QuerySource.Rollups ? Day0.AddDays(3) : Day0.AddDays(3).AddMinutes(-1);
      var result = engine.Query(new AnalyticsQuery
      {
        Event = "sample", Range = Range(start, end),
        Source = source, GroupBy = { "group" },
        Metrics = { Metric.Events, Metric.Visitors, Metric.Sum("x"), Metric.Average("x"), Metric.Variance("x"), Metric.Min("x"), Metric.Max("x"), Metric.First("x"), Metric.Last("x"), Metric.Distinct("identity") }
      });
      var included = events.Where(e => e.Time >= start && e.Time < end).ToArray();
      foreach (var row in result.Rows)
      {
        var values = included.Where(e => e.Group == row["group"]).ToArray();
        var mean = values.Average(e => e.Value);
        Assert.Multiple(() =>
        {
          Assert.That(row[Metric.Events], Is.EqualTo(values.Length));
          Assert.That(row[Metric.Visitors], Is.EqualTo(values.Select(e => e.Visitor).Distinct().Count()));
          Assert.That(row[Metric.Sum("x")], Is.EqualTo(values.Sum(e => e.Value)));
          Assert.That(row[Metric.Average("x")], Is.EqualTo(mean).Within(1e-9));
          Assert.That(row[Metric.Variance("x")], Is.EqualTo(values.Average(e => Math.Pow(e.Value - mean, 2))).Within(1e-7));
          Assert.That(row[Metric.Min("x")], Is.EqualTo(values.Min(e => e.Value)));
          Assert.That(row[Metric.Max("x")], Is.EqualTo(values.Max(e => e.Value)));
          Assert.That(row[Metric.First("x")], Is.EqualTo(values[0].Value));
          Assert.That(row[Metric.Last("x")], Is.EqualTo(values[^1].Value));
          Assert.That(row[Metric.Distinct("identity")], Is.EqualTo(values.Select(e => e.Identity).Distinct().Count()));
        });
      }
      Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(included.Select(e => e.Visitor).Distinct().Count()));
    }
  }
}
