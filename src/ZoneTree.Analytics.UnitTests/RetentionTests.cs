using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Retention by deletion predicates: expired records are hidden at once whatever the partition
/// size, and removed from disk by retention runs.
/// </summary>
public sealed class RetentionTests
{
  static readonly DateTime Old = DateTime.UtcNow.Date.AddDays(-10).AddHours(12);

  static readonly DateTime Recent = DateTime.UtcNow.Date.AddDays(-1).AddHours(12);

  string directory;

  [SetUp]
  public void SetUp() => directory = TestEngine.NewDirectory("retention");

  [TearDown]
  public void TearDown() => TestEngine.Delete(directory);

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  const int PerSite = 3_000;

  /// <summary>
  /// Small disk segment parts, so that merges copy parts without reading them, as they do in
  /// large trees.
  /// </summary>
  static void SmallParts(AnalyticsOptions options)
  {
    options.Storage.RawEvents.MutableSegmentMaxItemCount = 1_000;
    options.Storage.RawEvents.AllowUnsafeOptionValues = true;
    options.Storage.RawEvents.ConfigureDiskSegments = disk =>
    {
      disk.MinimumRecordCount = 500;
      disk.MaximumRecordCount = 1_000;
    };
  }

  static void ThreeDays(AnalyticsOptions options)
  {
    SmallParts(options);
    options.Retention.RawEvents = TimeSpan.FromDays(3);
    options.Retention.HourRollups = TimeSpan.FromDays(3);
  }

  /// <summary>
  /// Writes, with unlimited retention, sites whose keys interleave old and recent events: site
  /// "a" has only recent events, so the disk segment part that holds the old events of site "b"
  /// starts with newer records; site "c" has only old events.
  /// </summary>
  void WriteSites()
  {
    using var engine = TestEngine.Create(directory, SmallParts);
    void Track(string site, DateTime time)
    {
      for (var i = 0; i < PerSite; ++i)
        engine.Track(new AnalyticsEvent("e") { Site = site, VisitorId = site, Timestamp = Utc(time.AddSeconds(i)) });
    }
    Track("a", Recent);
    Track("b", Old);
    Track("b", Recent);
    Track("c", Old);
  }

  /// <summary>
  /// The records of a store, deleted ones included: what is still stored anywhere in its trees.
  /// </summary>
  static long StoredRecords<TValue>(PartitionedStore<Memory<byte>, TValue> store)
  {
    long count = 0;
    foreach (var name in store.ListPartitions())
    {
      using var lease = store.Acquire(name);
      using var iterator = lease.Tree.CreateIterator(new IteratorOptions { IncludeDeletedRecords = true });
      while (iterator.Next())
        ++count;
    }
    return count;
  }

  static long VisibleRecords<TValue>(PartitionedStore<Memory<byte>, TValue> store)
  {
    long count = 0;
    foreach (var name in store.ListPartitions())
    {
      using var lease = store.Acquire(name);
      using var iterator = lease.Tree.CreateIterator();
      while (iterator.Next())
        ++count;
    }
    return count;
  }

  static long EventsOn(AnalyticsEngine engine, string site, DateTime day) => (long)engine.Query(new AnalyticsQuery
  {
    Site = site,
    Event = "e",
    Range = DateRange.Between(Utc(day.Date), Utc(day.Date.AddDays(1))),
    Source = QuerySource.RawEvents,
    Metrics = { Metric.Events }
  }).Totals[Metric.Events];

  [Test]
  public void RangesExpireOnlyWithinOneHead()
  {
    const long cutoff = 1_000;
    ExpiredKeyPredicate rawExpired = key => KeyCodec.ReadRawTicks(key) < cutoff;
    ExpiredKeyPredicate rollupExpired = key => KeyCodec.ReadRollupBucket(key) < cutoff;
    bool Raw(Memory<byte> first, Memory<byte> last) => ExpiredRecords.IsRangeExpired(first.Span, last.Span, 1, rawExpired);
    bool Rollups(Memory<byte> first, Memory<byte> last) => ExpiredRecords.IsRangeExpired(first.Span, last.Span, 3, rollupExpired);
    Assert.Multiple(() =>
    {
      // One site, older than the cutoff up to the last key: every event between is older too.
      Assert.That(Raw(KeyCodec.RawKey("a", 10, 1), KeyCodec.RawKey("a", 999, 7)), Is.True);
      Assert.That(Raw(KeyCodec.RawKey("a", 10, 1), KeyCodec.RawKey("a", 1_000, 1)), Is.False);
      // Across sites the range holds the newest events of the first site.
      Assert.That(Raw(KeyCodec.RawKey("a", 10, 1), KeyCodec.RawKey("b", 20, 1)), Is.False);
      // A site that prefixes another is a different head.
      Assert.That(Raw(KeyCodec.RawKey("sit", 10, 1), KeyCodec.RawKey("site", 20, 1)), Is.False);
      Assert.That(Rollups(KeyCodec.RollupKey("s", "e", "page", 10, "/a"), KeyCodec.RollupKey("s", "e", "page", 999, "/z")), Is.True);
      Assert.That(Rollups(KeyCodec.RollupKey("s", "e", "page", 10, "/a"), KeyCodec.RollupKey("s", "e", "page", 1_000, "/a")), Is.False);
      Assert.That(Rollups(KeyCodec.RollupKey("s", "e", "", 10, ""), KeyCodec.RollupKey("s", "e", "page", 20, "/a")), Is.False);
      Assert.That(Rollups(KeyCodec.RollupKey("s", "e", "page", 10, "/a"), KeyCodec.RollupKey("s", "f", "page", 20, "/a")), Is.False);
    });
  }

  [Test]
  public void CutoffsMoveInWholeDaysOrHours()
  {
    var now = new DateTime(2026, 10, 10, 15, 42, 7, DateTimeKind.Utc).Ticks;
    var cutoffs = RetentionCutoffs.Compute(
        new RetentionOptions { RawEvents = TimeSpan.FromDays(3), MinuteRollups = TimeSpan.FromHours(6), HourRollups = null },
        new ReportingCalendar(TimeZoneInfo.Utc, DayOfWeek.Monday),
        now);
    Assert.Multiple(() =>
    {
      Assert.That(new DateTime(cutoffs.RawEvents), Is.EqualTo(new DateTime(2026, 10, 7)));
      Assert.That(new DateTime(cutoffs.MinuteRollups), Is.EqualTo(new DateTime(2026, 10, 10, 9, 0, 0)));
      Assert.That(cutoffs.HourRollups, Is.EqualTo(long.MinValue));
    });
  }

  [Test]
  public void ExpiredRecordsAreHiddenAtOnceInYearlyPartitions()
  {
    WriteSites();
    using var engine = TestEngine.Create(directory, ThreeDays);
    var raw = engine.RawEventStore.Store;
    var hourly = engine.RollupStore.GetStore(Granularity.Hour);
    Assert.Multiple(() =>
    {
      // No retention run yet: the records are still stored, but no reader sees them.
      Assert.That(StoredRecords(raw), Is.EqualTo(4 * PerSite));
      Assert.That(VisibleRecords(raw), Is.EqualTo(2 * PerSite));
      Assert.That(VisibleRecords(hourly), Is.LessThan(StoredRecords(hourly)));
      Assert.That(EventsOn(engine, "b", Old), Is.EqualTo(0));
      Assert.That(EventsOn(engine, "b", Recent), Is.EqualTo(PerSite));
      Assert.That(EventsOn(engine, "c", Old), Is.EqualTo(0));
      Assert.That(EventsOn(engine, "a", Recent), Is.EqualTo(PerSite));
    });
  }

  [Test]
  public void RetentionRemovesExpiredRecordsFromDisk()
  {
    WriteSites();
    using var engine = TestEngine.Create(directory, ThreeDays);
    var raw = engine.RawEventStore.Store;
    var hourly = engine.RollupStore.GetStore(Granularity.Hour);
    var visibleHourly = VisibleRecords(hourly);
    engine.ApplyRetention();
    Assert.Multiple(() =>
    {
      Assert.That(StoredRecords(raw), Is.EqualTo(2 * PerSite), "only the recent events of sites a and b remain");
      Assert.That(StoredRecords(hourly), Is.EqualTo(visibleHourly));
      Assert.That(EventsOn(engine, "b", Recent), Is.EqualTo(PerSite));
      Assert.That(EventsOn(engine, "a", Recent), Is.EqualTo(PerSite));
    });
    // Nothing expired is left, so retention runs delete nothing until the cutoff moves.
    var options = TestEngine.Options(directory, ThreeDays);
    var clock = new RetentionClock(options.Retention, new ReportingCalendar(options.TimeZone, options.FirstDayOfWeek));
    foreach (var name in raw.ListPartitions())
    {
      using var lease = raw.Acquire(name);
      Assert.That(ExpiredRecords.DeleteFirstOfExpiredHeads(lease.Tree, 1, clock.IsRawEventExpired), Is.Zero);
    }
  }

  [Test]
  public void ExpiredDataIsGoneAfterReopening()
  {
    WriteSites();
    using (var engine = TestEngine.Create(directory, ThreeDays))
      engine.ApplyRetention();
    // Even with retention lifted again, removed data does not come back.
    using var reopened = TestEngine.Create(directory, SmallParts);
    Assert.Multiple(() =>
    {
      Assert.That(StoredRecords(reopened.RawEventStore.Store), Is.EqualTo(2 * PerSite));
      Assert.That(EventsOn(reopened, "c", Old), Is.EqualTo(0));
    });
  }

  [Test]
  public void BottomSegmentsAreMergedAutomatically()
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.RawEvents.MutableSegmentMaxItemCount = 1_000;
      o.Storage.RawEvents.DiskSegmentMaxItemCount = 2_000;
      o.Storage.RawEvents.AllowUnsafeOptionValues = true;
    });
    for (var i = 0; i < 12_000; ++i)
      engine.Track(new AnalyticsEvent("e") { VisitorId = "v", Timestamp = Utc(Recent.AddSeconds(i)) });
    engine.Compact();
    var raw = engine.RawEventStore.Store;
    var name = raw.ListPartitions().Single();
    using var lease = raw.Acquire(name);
    Assert.Multiple(() =>
    {
      // Without automatic merges, every disk segment past 2,000 records would stay a bottom segment.
      Assert.That(lease.Tree.Maintenance.BottomSegments, Has.Count.LessThanOrEqualTo(2));
      Assert.That(lease.Tree.Count(), Is.EqualTo(12_000));
    });
  }
}
