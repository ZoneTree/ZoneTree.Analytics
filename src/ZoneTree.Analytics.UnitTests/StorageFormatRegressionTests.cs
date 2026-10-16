using System.Globalization;
using System.Text.RegularExpressions;
using ZoneTree.Analytics.Privacy;
using ZoneTree.Analytics.Query;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Regressions of the storage format review: each test fails on the format before its fix.
/// </summary>
public sealed class StorageFormatRegressionTests : DirectoryTest
{
  static readonly DateTime First = DateTime.UtcNow.Date.AddDays(-20).AddHours(10);

  static string DayName(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  string[] Partitions(params string[] path) =>
      Directory.GetDirectories(Path.Combine([directory, .. path])).Select(Path.GetFileName).Order().ToArray();

  AnalyticsEngine RawPartitionsOfOneRecord() => TestEngine.Create(directory, o =>
  {
    o.TimeZone = TimeZoneInfo.Utc;
    o.Storage.Partitioning.RawEvents = 1;
  });

  static void View(AnalyticsEngine engine, DateTime time)
  {
    Assert.That(engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "v", Timestamp = Utc(time) }), Is.True);
    engine.Flush();
  }

  static double RawPageViews(AnalyticsEngine engine, DateTime from, DateTime to) => engine.Query(new AnalyticsQuery
  {
    Range = Range(from, to), Source = QuerySource.RawEvents, Metrics = { Metric.PageViews }
  }).Totals[Metric.PageViews];

  [TestCase(StorageFormats.DatasetKey, "1", "older release")]
  [TestCase(StorageFormats.DatasetKey, "3", "newer release")]
  [TestCase(StorageFormats.DatasetKey, "two", "unknown storage format")]
  [TestCase(StorageFormats.DimensionIndexKey, "1", "older release")]
  [TestCase(StorageFormats.DimensionIndexKey, "3", "newer release")]
  [TestCase(StorageFormats.RollupIndexKey, "1", "older release")]
  [TestCase(StorageFormats.RollupIndexKey, "two", "unknown storage format")]
  public void DirectoriesOfOtherStorageFormatsAreRefused(string key, string stored, string explanation)
  {
    using (TestEngine.Create(directory)) { }
    var meta = new MetaStore(Path.Combine(directory, "meta"));
    Assert.That(meta.Get(key), Is.Not.Null, "every format is persisted, even for unused indexes");
    meta.Set(key, stored);
    var error = Assert.Throws<AnalyticsConfigurationException>(() => TestEngine.Create(directory).Dispose());
    Assert.That(error.Message, Does.Contain(explanation));
  }

  [Test]
  public void RolloverNeverHidesRecordsWrittenAfterItsClock()
  {
    // A record from a clock that ran ahead: later than the housekeeping clock below.
    var ahead = First.AddDays(5);
    using (var engine = RawPartitionsOfOneRecord())
    {
      View(engine, First);
      View(engine, ahead);
    }
    // The high-water mark of the partition survives reopening.
    using var reopened = RawPartitionsOfOneRecord();
    View(reopened, First.AddMinutes(1));
    Assert.That(reopened.RollOverPartitions(First.Ticks), Is.EqualTo(1));
    Assert.Multiple(() =>
    {
      Assert.That(Partitions("events"), Is.EqualTo(new[] { DayName(First), DayName(ahead.Date.AddDays(1)) }));
      Assert.That(RawPageViews(reopened, ahead.Date, ahead.Date.AddDays(1)), Is.EqualTo(1));
      Assert.That(RawPageViews(reopened, First.Date, ahead.Date.AddDays(1)), Is.EqualTo(3));
    });
  }

  [Test]
  public void RolloverResumesAfterAClockThatRanAheadCreatedAnEmptyFuturePartition()
  {
    using var engine = RawPartitionsOfOneRecord();
    View(engine, First);
    // Housekeeping ran while the clock read ten days later.
    Assert.That(engine.RollOverPartitions(First.AddDays(10).Ticks), Is.EqualTo(1));
    var future = First.Date.AddDays(12);
    // The clock is correct again: events still go to the first partition.
    View(engine, First.AddMinutes(1));
    Assert.That(engine.RollOverPartitions(First.Ticks), Is.EqualTo(1), "the partition receiving events rolls over");
    var next = First.Date.AddDays(2);
    View(engine, next.AddMinutes(1));
    Assert.Multiple(() =>
    {
      Assert.That(Partitions("events"), Is.EqualTo(new[] { DayName(First), DayName(next), DayName(future) }));
      Assert.That(engine.GetStatistics().Partitions.Single(x => x.Directory == "events/" + DayName(next)).ApproximateRecordCount, Is.EqualTo(1));
      Assert.That(RawPageViews(engine, First.Date, future.AddDays(1)), Is.EqualTo(3));
    });
  }

  [Test]
  public void AnonymousHistoricalVisitorsKeepTheirSaltAcrossRotationAndRestart()
  {
    var day = DateTime.UtcNow.Date.AddDays(-10);
    void Visit(AnalyticsEngine engine, int hour)
    {
      engine.Track(new AnalyticsEvent(StandardEvents.PageView)
      {
        Url = "/", IpAddress = "10.0.0.9", UserAgent = UserAgents.ChromeWindows, Timestamp = Utc(day.AddHours(hour))
      });
      engine.Flush();
    }
    using (var engine = TestEngine.Create(directory))
    {
      Visit(engine, 1);
      // Retention rotates the salts while the backfill continues.
      engine.ApplyRetention();
      Visit(engine, 2);
    }
    using var reopened = TestEngine.Create(directory);
    Visit(reopened, 3);
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups })
    {
      var result = reopened.Query(new AnalyticsQuery { Range = Day(day), Source = source, Metrics = { Metric.Visitors, Metric.PageViews } });
      Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(3), source.ToString());
      Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(1), source.ToString());
    }
  }

  [Test]
  public void UnusedHistoricalSaltsAreDestroyedADayAfterTheirLastUse()
  {
    var meta = new MetaStore(Path.Combine(directory, "meta"));
    var hasher = new VisitorHasher(VisitorIdentification.DailyRotatingHash, null, meta);
    var old = DateTime.UtcNow.Date.AddDays(-10);
    var saltKey = "salt:" + DayName(old);
    var hash = hasher.Compute("s", null, "ip", "ua", old.AddHours(1).Ticks);
    hasher.RotateSalts(DateTime.UtcNow);
    Assert.Multiple(() =>
    {
      Assert.That(meta.Get(saltKey), Is.Not.Null, "a salt in use survives rotation");
      Assert.That(new VisitorHasher(VisitorIdentification.DailyRotatingHash, null, new MetaStore(Path.Combine(directory, "meta")))
          .Compute("s", null, "ip", "ua", old.AddHours(2).Ticks), Is.EqualTo(hash), "and reopening");
    });
    hasher.RotateSalts(DateTime.UtcNow.AddDays(2));
    Assert.Multiple(() =>
    {
      Assert.That(meta.ListByPrefix("salt").Any(x => x.Key.EndsWith(DayName(old), StringComparison.Ordinal)), Is.False);
      Assert.That(hasher.Compute("s", null, "ip", "ua", old.AddHours(3).Ticks), Is.Not.EqualTo(hash), "the destroyed salt is not reused");
    });
  }

  [Test]
  public void InvalidUtf16IsCountedAsTheValueItIsStoredAs()
  {
    var day = DateTime.UtcNow.Date.AddDays(-2);
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Rollups.Definitions.Add(new RollupDefinition("plan"));
      o.Rollups.DistinctDimensions.Add("plan");
    });
    // Two different lone surrogates both encode as U+FFFD in UTF-8.
    foreach (var plan in new[] { "x" + (char)0xD800, "x" + (char)0xDBFF })
    {
      engine.Track(new AnalyticsEvent("purchase")
      {
        VisitorId = "v", Timestamp = Utc(day.AddHours(1)), Dimensions = { ["plan"] = plan }
      });
    }
    engine.Flush();
    foreach (var source in new[] { QuerySource.Rollups, QuerySource.RawEvents })
    {
      var distinct = engine.Query(new AnalyticsQuery
      {
        Event = "purchase", Range = Day(day), Source = source, Metrics = { Metric.Distinct("plan") }
      });
      var grouped = engine.Query(new AnalyticsQuery
      {
        Event = "purchase", Range = Day(day), Source = source, GroupBy = { "plan" }, Metrics = { Metric.Events }
      });
      Assert.Multiple(() =>
      {
        Assert.That(distinct.Totals[Metric.Distinct("plan")], Is.EqualTo(1), source.ToString());
        Assert.That(grouped.Rows.Single()["plan"], Is.EqualTo("x�"), source.ToString());
        Assert.That(grouped.Rows.Single()[Metric.Events], Is.EqualTo(2), source.ToString());
      });
    }
  }

  // Escaped: the test adapter does not run cases whose arguments contain control characters.
  [TestCase(@"a\u001Fb")]
  [TestCase(@"\u0001")]
  [TestCase(@"tab\tvalue")]
  public void RawCodecRejectsControlCharactersReservedByRollupKeys(string value)
  {
    value = Regex.Unescape(value);
    var raw = new RawEvent { Site = "s", Ticks = 1000, Sequence = 1, Name = "e", Dimensions = { new("plan", value) } };
    Assert.Throws<ArgumentException>(() => RawEventCodec.Encode(raw));
    // Bytes written without the public encoder do not decode either.
    var bytes = RawEventCodec.Encode("e", 1000, 0, 0, 0, false, [new("plan", value)], []);
    Assert.Throws<FormatException>(() => RawEventCodec.Decode(RawEventCodec.EncodeKey(raw).Span, bytes.Span));
  }

  [Test]
  public void SessionStoreAcceptsAStateWithoutDimensions()
  {
    using (var engine = TestEngine.Create(directory))
      engine.Storage.Sessions.Upsert(42, new SessionState { SessionId = 7, ExpiresAtUtcTicks = DateTime.UtcNow.AddHours(1).Ticks });
    using var reopened = TestEngine.Create(directory);
    Assert.That(reopened.Storage.Sessions.TryGet(42, out var stored), Is.True);
    Assert.Multiple(() =>
    {
      Assert.That(stored.SessionId, Is.EqualTo(7));
      Assert.That(stored.Dimensions, Has.Length.EqualTo(StandardDimensions.SessionLevel.Count));
      Assert.That(stored.Dimensions, Is.All.Null);
    });
  }

  [Test]
  public void JointBackfillJournalRoundTripsEveryField()
  {
    var checkpoint = new JointBackfillCheckpoint(directory, "configuration", 5);
    var state = checkpoint.State;
    state.Copying = true;
    state.Site = "shop";
    state.Partition = "2026-10-15";
    state.Key = [1, 2, 3];
    state.Events = 10;
    state.Sessions = 11;
    state.Updates = 12;
    state.Flushes = 13;
    state.Sites = 2;
    state.Active[9] = new RawSession
    {
      Start = 100, Last = 200, PageViews = 3, Interactions = 4, Visitor = 77, Allowed = true,
      Dimensions = new(StringComparer.Ordinal) { ["source"] = "GitHub", ["country"] = null },
      HasStartEvent = true, FirstIsPageView = true, FirstPage = "/a", FirstHostname = null,
      LastPage = "/b", LastHostname = "example.com", LastPageTicks = 150
    };
    state.Active[10] = new RawSession { Start = 1, Last = 2 };
    state.Redo.Add(new JointBackfillReplacement { Granularity = Granularity.Day, Partition = "2026-10-14", Key = [4], Value = [5, 6] });
    checkpoint.Save();

    var reopened = new JointBackfillCheckpoint(directory, "configuration", 99);
    var loaded = reopened.State;
    var session = loaded.Active[9];
    Assert.Multiple(() =>
    {
      Assert.That(reopened.Resuming, Is.True);
      Assert.That((loaded.Copying, loaded.Coverage, loaded.Site, loaded.SiteCompleted, loaded.Partition), Is.EqualTo((true, 5L, "shop", false, "2026-10-15")));
      Assert.That(loaded.Key, Is.EqualTo(new byte[] { 1, 2, 3 }));
      Assert.That((loaded.Events, loaded.Sessions, loaded.Updates, loaded.Flushes, loaded.Sites), Is.EqualTo((10L, 11L, 12L, 13L, 2)));
      Assert.That((session.Start, session.Last, session.PageViews, session.Interactions, session.Visitor, session.Allowed), Is.EqualTo((100L, 200L, 3, 4, 77UL, true)));
      Assert.That(session.Dimensions, Is.EqualTo(state.Active[9].Dimensions));
      Assert.That((session.HasStartEvent, session.FirstIsPageView, session.FirstPage, session.FirstHostname), Is.EqualTo((true, true, "/a", (string)null)));
      Assert.That((session.LastPage, session.LastHostname, session.LastPageTicks), Is.EqualTo(("/b", "example.com", 150L)));
      Assert.That(loaded.Active[10].Dimensions, Is.Null);
      Assert.That(loaded.Redo.Single().Granularity, Is.EqualTo(Granularity.Day));
      Assert.That(loaded.Redo.Single().Partition, Is.EqualTo("2026-10-14"));
      Assert.That(loaded.Redo.Single().Key, Is.EqualTo(new byte[] { 4 }));
      Assert.That(loaded.Redo.Single().Value, Is.EqualTo(new byte[] { 5, 6 }));
      Assert.That(new JointBackfillCheckpoint(directory, "other", 5).Resuming, Is.False);
    });
    reopened.Complete();
    Assert.That(new JointBackfillCheckpoint(directory, "configuration", 5).Resuming, Is.False);
  }

  [Test]
  public void JointBackfillJournalHoldsAFlushOfDenseRows()
  {
    // 7,500 rows with one dense 16,386-byte visitor sketch each: about 123 MB as bytes, more
    // than the largest string a JSON writer accepts once encoded as base64 inside JSON.
    const int rows = 7_500;
    var value = new byte[16_386];
    new Random(1).NextBytes(value);
    var checkpoint = new JointBackfillCheckpoint(directory, "configuration", 0);
    for (var i = 0; i < rows; ++i)
      checkpoint.State.Redo.Add(new JointBackfillReplacement { Granularity = Granularity.Hour, Partition = "2026-10-15", Key = BitConverter.GetBytes(i), Value = value });
    checkpoint.Save();
    var journal = Directory.GetFiles(Path.Combine(directory, "backfill", "joint-rollups")).Sum(path => new FileInfo(path).Length);
    Assert.That(journal, Is.LessThan(rows * (value.Length + 64L)), "the journal stores bytes, not text");
    var redo = new JointBackfillCheckpoint(directory, "configuration", 0).State.Redo;
    Assert.That(redo, Has.Count.EqualTo(rows));
    Assert.That(redo[^1].Key, Is.EqualTo(BitConverter.GetBytes(rows - 1)));
    Assert.That(redo[^1].Value, Is.EqualTo(value));
  }

  [Test]
  public void AJointBackfillJournalOfAnOlderReleaseIsRefused()
  {
    // A valid journal file whose body starts with an older version.
    var body = BitConverter.GetBytes(1);
    var header = new byte[56];
    "ZTAJRNL1"u8.CopyTo(header);
    BitConverter.GetBytes(1L).CopyTo(header, 8);
    BitConverter.GetBytes((long)body.Length).CopyTo(header, 16);
    System.Security.Cryptography.SHA256.HashData(body).CopyTo(header, 24);
    var journal = Path.Combine(directory, "backfill", "joint-rollups");
    Directory.CreateDirectory(journal);
    File.WriteAllBytes(Path.Combine(journal, "journal-b.bin"), [.. header, .. body]);
    var error = Assert.Throws<AnalyticsConfigurationException>(() => _ = new JointBackfillCheckpoint(directory, "configuration", 0));
    Assert.That(error.Message, Does.Contain("older release"));
  }

  [Test]
  public void ATornJointBackfillJournalFallsBackToThePreviousCheckpoint()
  {
    var checkpoint = new JointBackfillCheckpoint(directory, "configuration", 0);
    checkpoint.State.Events = 1;
    checkpoint.Save();
    checkpoint.State.Events = 2;
    checkpoint.Save();
    // The newest file has the highest generation, stored after the 8-byte magic. Write times
    // cannot tell the files apart: on Linux both saves can share a timestamp.
    var newest = Directory.GetFiles(Path.Combine(directory, "backfill", "joint-rollups"))
        .OrderByDescending(path => BitConverter.ToInt64(File.ReadAllBytes(path), 8)).First();
    var bytes = File.ReadAllBytes(newest);
    File.WriteAllBytes(newest, bytes[..(bytes.Length - 1)]);
    Assert.That(new JointBackfillCheckpoint(directory, "configuration", 0).State.Events, Is.EqualTo(1));
  }
}
