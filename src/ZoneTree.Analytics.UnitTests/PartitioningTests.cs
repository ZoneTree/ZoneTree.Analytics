using System.Globalization;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class PartitioningTests
{
  static readonly DateTime First = DateTime.UtcNow.Date.AddDays(-20).AddHours(10);

  static readonly DateTime Second = DateTime.UtcNow.Date.AddDays(-5).AddHours(10);

  /// <summary>
  /// The start of the partitions that <see cref="AnalyticsEngine.RollOverPartitions"/> starts at
  /// <see cref="First"/>: the day boundary after one hour of future skew and one day.
  /// </summary>
  static readonly DateTime NextStart = First.Date.AddDays(2);

  string directory;

  [SetUp]
  public void SetUp() => directory = TestEngine.NewDirectory("partitioning");

  [TearDown]
  public void TearDown() => TestEngine.Delete(directory);

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  AnalyticsEngine Create(Action<PartitioningOptions> partitioning, Action<AnalyticsOptions> configure = null) =>
      TestEngine.Create(directory, o =>
      {
        o.TimeZone = TimeZoneInfo.Utc;
        o.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
        partitioning(o.Storage.Partitioning);
        configure?.Invoke(o);
      });

  static void Track(AnalyticsEngine engine, DateTime time, string page = "/", string visitor = "1")
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = page, VisitorId = visitor, Timestamp = Utc(time) });
    engine.Flush();
  }

  string[] Partitions(params string[] path) =>
      Directory.GetDirectories(Path.Combine([directory, .. path])).Select(Path.GetFileName).Order().ToArray();

  static void AllTiersOfOneRecord(PartitioningOptions p)
  {
    p.RawEvents = 1;
    p.MinuteRollups = 1;
    p.HourRollups = 1;
    p.DayRollups = 1;
  }

  static readonly string[][] Tiers = [["events"], ["rollups", "minute"], ["rollups", "hour"], ["rollups", "day"]];

  [Test]
  public void EachTierStartsWithOnePartition()
  {
    using (var engine = Create(_ => { }))
    {
      Track(engine, First, "/a");
      Track(engine, Second, "/b");
      Assert.That(engine.RollOverPartitions(Second.Ticks), Is.EqualTo(0));
    }
    Assert.Multiple(() =>
    {
      // The first record of a tier names its partition.
      foreach (var tier in Tiers)
        Assert.That(Partitions(tier), Is.EqualTo(new[] { Day(First) }), string.Join("/", tier));
    });
  }

  [Test]
  public void TiersStartNewPartitionsAfterTheirRecordCount()
  {
    using var engine = Create(p => p.RawEvents = 2);
    Track(engine, First);
    Assert.That(engine.RollOverPartitions(First.Ticks), Is.EqualTo(0), "one raw event of two");
    Track(engine, First.AddMinutes(1));
    Assert.That(engine.RollOverPartitions(First.Ticks), Is.EqualTo(1));
    Assert.That(engine.RollOverPartitions(First.Ticks), Is.EqualTo(0), "the new partition is empty");
    // Older events still belong to the first partition, newer ones to the new partition.
    Track(engine, Second);
    Track(engine, NextStart.AddMinutes(-1));
    Assert.Multiple(() =>
    {
      Assert.That(Partitions("events"), Is.EqualTo(new[] { Day(First), Day(NextStart) }));
      Assert.That(Partitions("rollups", "hour"), Is.EqualTo(new[] { Day(First) }));
      var partitions = engine.GetStatistics().Partitions.ToDictionary(x => x.Directory, x => x.ApproximateRecordCount);
      Assert.That(partitions["events/" + Day(First)], Is.EqualTo(3));
      Assert.That(partitions["events/" + Day(NextStart)], Is.EqualTo(1));
      Assert.That(partitions["rollups/hour/" + Day(First)], Is.GreaterThan(0));
      Assert.That(partitions.Keys, Has.Exactly(2).StartWith("events/"));
      Assert.That(engine.Query(new AnalyticsQuery
      {
        Range = DateRange.Between(Utc(First.Date), Utc(Second.Date.AddDays(1))),
        Source = QuerySource.RawEvents,
        Metrics = { Metric.PageViews },
      }).Totals[Metric.PageViews], Is.EqualTo(4));
    });
  }

  [Test]
  public void NewPartitionsStartAfterEveryEventTheEngineCanReceive()
  {
    using var engine = Create(p => p.RawEvents = 1, o => o.Ingestion.MaxFutureSkew = TimeSpan.FromHours(20));
    Track(engine, First);
    Assert.That(engine.RollOverPartitions(First.Ticks), Is.EqualTo(1));
    // An event received at First may be up to 20 hours ahead; a daily rollup bucket of a time
    // zone east of UTC up to a day ahead.
    Assert.That(Partitions("events"), Is.EqualTo(new[] { Day(First), Day(First.Date.AddDays(3)) }));
  }

  [Test]
  public void PartitionsHoldContiguousTimeRanges()
  {
    var options = TestEngine.Options(directory);
    var factory = new StoreFactory(
        options.Storage,
        Path.GetFullPath(directory),
        new RecordingLogger(),
        new RetentionClock(options.Retention, new ReportingCalendar(options.TimeZone, options.FirstDayOfWeek)));
    var day = TimeSpan.TicksPerDay;
    var t0 = new DateTime(2026, 3, 10, 12, 0, 0).Ticks;
    using var store = new PartitionedStore<Memory<byte>, Memory<byte>>(
        Path.Combine(directory, "events"), 1, factory.OpenRawEvents, new RecordingLogger(), factory.CreateRawEventsMaintainer, factory.WriteAheadLogDirectoryOf);
    void Write(long ticks)
    {
      using var lease = store.Acquire(store.Route(ticks));
      lease.Tree.Upsert(KeyCodec.RawKey("s", ticks, 1), new byte[] { 1 });
    }

    Assert.That(store.RollOver(t0), Is.False, "no partition yet");
    Write(t0);
    Assert.That(store.ListPartitions(), Is.EqualTo(new[] { "2026-03-10" }));
    // Rolling over at a time within a day starts the next partition at the next day boundary.
    Assert.That(store.RollOver(t0 + day), Is.True);
    var second = new DateTime(2026, 3, 12).Ticks;
    Assert.Multiple(() =>
    {
      Assert.That(store.ListPartitions(), Is.EqualTo(new[] { "2026-03-10", "2026-03-12" }));
      // The first partition holds every older record, the last every newer one.
      Assert.That(store.Route(t0 - 100 * day), Is.EqualTo("2026-03-10"));
      Assert.That(store.Route(second - 1), Is.EqualTo("2026-03-10"));
      Assert.That(store.Route(second), Is.EqualTo("2026-03-12"));
      Assert.That(store.Route(second + 100 * day), Is.EqualTo("2026-03-12"));
      Assert.That(store.PartitionsInRange(t0, second), Is.EqualTo(new[] { new PartitionRange("2026-03-10", long.MinValue, second) }));
      Assert.That(store.PartitionsInRange(second - 1, second + 1).Select(x => x.Name), Is.EqualTo(new[] { "2026-03-10", "2026-03-12" }));
      Assert.That(store.PartitionsInRange(second, long.MaxValue), Is.EqualTo(new[] { new PartitionRange("2026-03-12", second, long.MaxValue) }));
      Assert.That(store.RollOver(t0 + day), Is.False, "the latest partition is empty");
    });

    // A new partition always starts at least a day after the latest one.
    Write(second);
    Assert.That(store.RollOver(t0), Is.True);
    Assert.That(store.ListPartitions(), Is.EqualTo(new[] { "2026-03-10", "2026-03-12", "2026-03-13" }));

    // Only partitions that end at or before the cutoff are dropped; the latest never ends.
    Assert.That(store.DropPartitionsEndingBefore(second - 1), Is.EqualTo(0));
    Assert.That(store.DropPartitionsEndingBefore(long.MaxValue), Is.EqualTo(2));
    Assert.Multiple(() =>
    {
      Assert.That(store.ListPartitions(), Is.EqualTo(new[] { "2026-03-13" }));
      Assert.That(store.Route(t0), Is.EqualTo("2026-03-13"));
      Assert.That(store.Acquire("2026-03-10"), Is.Null, "a dropped partition is not recreated");
      Assert.That(Directory.Exists(Path.Combine(directory, "events", "2026-03-10")), Is.False);
    });
  }

  [Test]
  public void QueriesSpanPartitions()
  {
    using var engine = Create(AllTiersOfOneRecord);
    Track(engine, First, "/a", "1");
    Assert.That(engine.RollOverPartitions(First.Ticks), Is.EqualTo(4));
    Track(engine, Second, "/b", "2");
    foreach (var tier in Tiers)
      Assert.That(Partitions(tier), Is.EqualTo(new[] { Day(First), Day(NextStart) }), string.Join("/", tier));
    var range = DateRange.Between(Utc(First.Date), Utc(Second.Date.AddDays(1)));
    // Unaligned edges exercise day, hour and minute rollups together.
    var unaligned = DateRange.Between(Utc(First.AddMinutes(-30)), Utc(Second.AddMinutes(1)));
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
        Assert.That(daily.Rows.Where(x => x[Metric.PageViews] > 0).Select(x => x.Bucket), Is.EqualTo(new DateTimeOffset?[] { Utc(First.Date), Utc(Second.Date) }), source.ToString());
        Assert.That(daily.Totals[Metric.Visitors], Is.EqualTo(2), source.ToString());
        Assert.That(byPage.Rows.Select(x => x[StandardDimensions.Page]), Is.EquivalentTo(new[] { "/a", "/b" }), source.ToString());
      });
    }
  }

  [Test]
  public void PartitionSizeCanChangeForStoredData()
  {
    using (var engine = Create(_ => { }))
      Track(engine, First);
    using (var engine = Create(p => p.RawEvents = 1))
    {
      Assert.That(engine.GetStatistics().Partitions.Select(x => x.ApproximateRecordCount), Is.All.Null, "not opened yet");
      Track(engine, First.AddMinutes(1));
      Assert.That(engine.RollOverPartitions(First.Ticks), Is.EqualTo(1));
      Assert.That(engine.GetStatistics().Partitions.Single(x => x.Directory == "events/" + Day(First)).ApproximateRecordCount, Is.EqualTo(2));
    }
  }

  [Test]
  public void ExpiredPartitionsAreDropped()
  {
    var old = DateTime.UtcNow.Date.AddDays(-10).AddHours(12);
    var recent = DateTime.UtcNow.Date.AddDays(-1).AddHours(12);
    using (var writer = Create(p => p.RawEvents = 1))
    {
      Track(writer, old);
      Assert.That(writer.RollOverPartitions(old.Ticks), Is.EqualTo(1));
      Track(writer, recent);
    }
    // Retention is shortened afterwards.
    using var engine = Create(
        p => p.RawEvents = 1,
        o => o.Retention.RawEvents = TimeSpan.FromDays(3));
    Assert.That(engine.ApplyRetentionCountingDrops(), Is.EqualTo(1));
    int RawEvents(DateTime day) => (int)engine.Query(new AnalyticsQuery
    {
      Range = DateRange.Between(Utc(day.Date), Utc(day.Date.AddDays(1))),
      Source = QuerySource.RawEvents,
      Metrics = { Metric.PageViews },
    }).Totals[Metric.PageViews];
    Assert.Multiple(() =>
    {
      Assert.That(RawEvents(old), Is.EqualTo(0));
      Assert.That(RawEvents(recent), Is.EqualTo(1));
      Assert.That(Partitions("events"), Is.EqualTo(new[] { Day(old.Date.AddDays(2)) }));
    });
  }

  [Test]
  public void StorageLifecycleLogsNoProblems()
  {
    var logger = new RecordingLogger();
    var old = DateTime.UtcNow.Date.AddDays(-10).AddHours(12);
    void Configure(AnalyticsOptions o) => o.Storage.Logger = logger;
    static void Partitioning(PartitioningOptions p)
    {
      p.RawEvents = 1;
      p.HourRollups = 1;
    }
    using (var engine = Create(Partitioning, Configure))
    {
      Track(engine, old);
      Assert.That(engine.RollOverPartitions(old.Ticks), Is.EqualTo(2));
      Track(engine, DateTime.UtcNow.AddMinutes(-5));
    }
    // Reopen, drop the old partitions through retention, query, close.
    using (var engine = Create(Partitioning, o =>
    {
      Configure(o);
      o.Retention.RawEvents = TimeSpan.FromDays(3);
      o.Retention.HourRollups = TimeSpan.FromDays(3);
    }))
    {
      Assert.That(engine.ApplyRetentionCountingDrops(), Is.EqualTo(2));
      engine.Query(new AnalyticsQuery { Range = DateRange.LastDays(30), Source = QuerySource.RawEvents });
    }
    Assert.That(logger.Problems, Is.Empty);
  }

  [Test]
  public void ValidationRejectsInvalidPartitioning()
  {
    var zero = Assert.Throws<ArgumentException>(() => Create(p => p.RawEvents = 0).Dispose());
    var negative = Assert.Throws<ArgumentException>(() => Create(p => p.HourRollups = -1).Dispose());
    Assert.Multiple(() =>
    {
      Assert.That(zero.Message, Does.Contain("RawEvents"));
      Assert.That(negative.Message, Does.Contain("HourRollups"));
    });
  }

  [Test]
  public void DefaultPartitionSize()
  {
    var partitioning = new AnalyticsOptions().Storage.Partitioning;
    Assert.That(
        new[] { partitioning.RawEvents, partitioning.MinuteRollups, partitioning.HourRollups, partitioning.DayRollups },
        Is.All.EqualTo(10_000_000));
  }
}
