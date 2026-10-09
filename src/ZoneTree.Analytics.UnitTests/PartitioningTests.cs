using System.Globalization;

namespace ZoneTree.Analytics.UnitTests;

public sealed class PartitioningTests
{
  // Two events on either side of a month boundary, both in the past.
  static readonly DateTime MonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);

  static readonly DateTime Before = MonthStart.AddHours(-14);

  static readonly DateTime After = MonthStart.AddHours(10);

  string directory;

  [SetUp]
  public void SetUp() => directory = TestEngine.NewDirectory("partitioning");

  [TearDown]
  public void TearDown() => TestEngine.Delete(directory);

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  static string Name(DateTime value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

  AnalyticsEngine Create(Action<PartitioningOptions> partitioning, Action<AnalyticsOptions> configure = null) =>
      TestEngine.Create(directory, o =>
      {
        o.TimeZone = TimeZoneInfo.Utc;
        o.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
        partitioning(o.Storage.Partitioning);
        configure?.Invoke(o);
      });

  static void TrackAcrossMonthBoundary(AnalyticsEngine engine)
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/a", VisitorId = "1", Timestamp = Utc(Before) });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/b", VisitorId = "2", Timestamp = Utc(After) });
    engine.Flush();
  }

  string[] Partitions(params string[] path) =>
      Directory.GetDirectories(Path.Combine([directory, .. path])).Select(Path.GetFileName).Order().ToArray();

  [Test]
  public void EachTierIsPartitionedAsConfigured()
  {
    using (var engine = Create(p =>
    {
      p.RawEvents = PartitionPeriod.Day;
      p.MinuteRollups = PartitionPeriod.Month;
      p.HourRollups = PartitionPeriod.Day;
      p.DayRollups = PartitionPeriod.Month;
    }))
    {
      TrackAcrossMonthBoundary(engine);
    }
    Assert.Multiple(() =>
    {
      Assert.That(Partitions("events"), Is.EqualTo(new[] { Name(Before, "yyyy-MM-dd"), Name(After, "yyyy-MM-dd") }));
      Assert.That(Partitions("rollups", "minute"), Is.EqualTo(new[] { Name(Before, "yyyy-MM"), Name(After, "yyyy-MM") }));
      Assert.That(Partitions("rollups", "hour"), Is.EqualTo(new[] { Name(Before, "yyyy-MM-dd"), Name(After, "yyyy-MM-dd") }));
      Assert.That(Partitions("rollups", "day"), Is.EqualTo(new[] { Name(Before, "yyyy-MM"), Name(After, "yyyy-MM") }));
    });
  }

  [Test]
  public void DefaultPartitioning()
  {
    using (var engine = Create(_ => { }))
    {
      TrackAcrossMonthBoundary(engine);
    }
    Assert.Multiple(() =>
    {
      Assert.That(Partitions("events"), Is.EqualTo(new[] { Name(Before, "yyyy-MM"), Name(After, "yyyy-MM") }));
      Assert.That(Partitions("rollups", "minute"), Is.EqualTo(new[] { Name(Before, "yyyy-MM-dd"), Name(After, "yyyy-MM-dd") }));
      Assert.That(Partitions("rollups", "hour"), Is.EqualTo(new[] { Name(Before, "yyyy-MM"), Name(After, "yyyy-MM") }));
      Assert.That(Partitions("rollups", "day"), Is.EqualTo(new[] { Name(Before, "yyyy"), Name(After, "yyyy") }.Distinct()));
    });
  }

  [TestCase(PartitionPeriod.Day, PartitionPeriod.Day, PartitionPeriod.Month)]
  [TestCase(PartitionPeriod.Month, PartitionPeriod.Month, PartitionPeriod.Year)]
  [TestCase(PartitionPeriod.Year, PartitionPeriod.Year, PartitionPeriod.Year)]
  public void QueriesSpanPartitionsOfAnySize(PartitionPeriod raw, PartitionPeriod rollups, PartitionPeriod dayRollups)
  {
    using var engine = Create(p =>
    {
      p.RawEvents = raw;
      p.MinuteRollups = rollups;
      p.HourRollups = rollups;
      p.DayRollups = dayRollups;
    });
    TrackAcrossMonthBoundary(engine);
    var range = DateRange.Between(Utc(Before.Date), Utc(After.Date.AddDays(1)));
    // Hour-aligned edges exercise day, hour and minute rollups together.
    var unaligned = DateRange.Between(Utc(Before.AddMinutes(-30)), Utc(After.AddMinutes(1)));
    foreach (var source in new[] { QuerySource.Rollups, QuerySource.RawEvents })
    {
      var daily = engine.Query(new AnalyticsQuery
      {
        Range = range,
        Interval = TimeInterval.Day,
        Source = source,
        Metrics = { Metric.PageViews, Metric.Visitors },
      });
      var byPage = engine.Query(new AnalyticsQuery
      {
        Range = unaligned,
        Source = source,
        GroupBy = { StandardDimensions.Page },
        Metrics = { Metric.PageViews },
      });
      Assert.Multiple(() =>
      {
        Assert.That(daily.Rows.Select(x => x[Metric.PageViews]), Is.EqualTo(new[] { 1.0, 1.0 }), source.ToString());
        Assert.That(daily.Totals[Metric.Visitors], Is.EqualTo(2), source.ToString());
        Assert.That(byPage.Rows.Select(x => x[StandardDimensions.Page]), Is.EquivalentTo(new[] { "/a", "/b" }), source.ToString());
      });
    }
  }

  [Test]
  public void PartitioningCannotChangeForStoredData()
  {
    using (var engine = Create(_ => { }))
    {
      TrackAcrossMonthBoundary(engine);
    }
    var hour = Assert.Throws<AnalyticsConfigurationException>(() =>
        Create(p => p.HourRollups = PartitionPeriod.Year).Dispose());
    var raw = Assert.Throws<AnalyticsConfigurationException>(() =>
        Create(p => p.RawEvents = PartitionPeriod.Day).Dispose());
    Assert.Multiple(() =>
    {
      Assert.That(hour.Message, Does.Contain("hourly rollup partitioning").And.Contain("'Month'").And.Contain("'Year'"));
      Assert.That(raw.Message, Does.Contain("raw event partitioning"));
    });
    Assert.DoesNotThrow(() => Create(_ => { }).Dispose());
  }

  [Test]
  public void PartitioningOfATierWithoutDataCanChange()
  {
    using (var engine = Create(_ => { }, o =>
    {
      o.Storage.StoreRawEvents = false;
      o.Rollups.Granularities = Granularity.Hour | Granularity.Day;
    }))
    {
      TrackAcrossMonthBoundary(engine);
    }
    using (var engine = Create(p =>
    {
      p.RawEvents = PartitionPeriod.Day;
      p.MinuteRollups = PartitionPeriod.Month;
    }))
    {
      TrackAcrossMonthBoundary(engine);
    }
    Assert.Multiple(() =>
    {
      Assert.That(Partitions("events"), Has.All.Length.EqualTo(10));
      Assert.That(Partitions("rollups", "minute"), Has.All.Length.EqualTo(7));
    });
    // Now that they hold data, the new values are fixed.
    Assert.Throws<AnalyticsConfigurationException>(() => Create(_ => { }).Dispose());
  }

  [Test]
  public void SmallerPartitionsMakeRetentionPrecise()
  {
    var old = DateTime.UtcNow.Date.AddDays(-10).AddHours(12);
    var recent = DateTime.UtcNow.Date.AddDays(-1).AddHours(12);
    using (var writer = Create(p => p.RawEvents = PartitionPeriod.Day))
    {
      writer.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(old) });
      writer.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(recent) });
    }
    // Retention is shortened afterwards.
    using var engine = Create(
        p => p.RawEvents = PartitionPeriod.Day,
        o => o.Retention.RawEvents = TimeSpan.FromDays(3));
    Assert.That(engine.ApplyRetention(), Is.EqualTo(1));
    int RawEvents(DateTime day) => (int)engine.Query(new AnalyticsQuery
    {
      Event = "e",
      Range = DateRange.Between(Utc(day.Date), Utc(day.Date.AddDays(1))),
      Source = QuerySource.RawEvents,
    }).Totals[Metric.Events];
    Assert.Multiple(() =>
    {
      Assert.That(RawEvents(old), Is.EqualTo(0));
      Assert.That(RawEvents(recent), Is.EqualTo(1));
      Assert.That(Partitions("events"), Is.EqualTo(new[] { Name(recent, "yyyy-MM-dd") }));
    });
  }

  [Test]
  public void ValidationRejectsPartitionsLongerThanRetention()
  {
    var minute = Assert.Throws<ArgumentException>(() => Create(
        p => p.MinuteRollups = PartitionPeriod.Month,
        o => o.Retention.MinuteRollups = TimeSpan.FromDays(2)).Dispose());
    var raw = Assert.Throws<ArgumentException>(() => Create(
        p => p.RawEvents = PartitionPeriod.Year,
        o => o.Retention.RawEvents = TimeSpan.FromDays(90)).Dispose());
    var daily = Assert.Throws<ArgumentException>(() => Create(p => p.DayRollups = PartitionPeriod.Day).Dispose());
    var invalid = Assert.Throws<ArgumentException>(() => Create(p => p.HourRollups = (PartitionPeriod)42).Dispose());
    Assert.Multiple(() =>
    {
      Assert.That(minute.Message, Does.Contain("Retention.MinuteRollups").And.Contain("month"));
      Assert.That(raw.Message, Does.Contain("Retention.RawEvents").And.Contain("year"));
      Assert.That(daily.Message, Does.Contain("DayRollups"));
      Assert.That(invalid.Message, Does.Contain("HourRollups"));
    });
  }

  [Test]
  public void ValidationAcceptsSensibleCombinations()
  {
    Assert.DoesNotThrow(() => Create(
        p => p.MinuteRollups = PartitionPeriod.Day,
        o => o.Retention.MinuteRollups = TimeSpan.FromHours(6)).Dispose(), "the smallest period always fits");
    TestEngine.Delete(directory);
    Assert.DoesNotThrow(() => Create(
        p => p.HourRollups = PartitionPeriod.Year,
        o => o.Retention.HourRollups = TimeSpan.FromDays(400)).Dispose());
    TestEngine.Delete(directory);
    Assert.DoesNotThrow(() => Create(
        p => p.MinuteRollups = PartitionPeriod.Year,
        o =>
        {
          o.Retention.MinuteRollups = TimeSpan.FromDays(2);
          o.Rollups.Granularities = Granularity.Hour | Granularity.Day;
        }).Dispose(), "disabled tiers are not validated");
  }
}
