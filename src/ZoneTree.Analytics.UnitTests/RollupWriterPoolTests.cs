using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class RollupWriterPoolTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-2);

  static Dictionary<RollupBufferKey, RollupAccumulator> Rows(RollupBufferKey key, ulong visitor)
  {
    var accumulator = new RollupAccumulator { Count = 1 };
    accumulator.AddVisitor(visitor, 14);
    return new() { [key] = accumulator };
  }

  static StoreFactory Factory(AnalyticsOptions options) => new(options.Storage,
      Path.GetFullPath(options.DataDirectory), new RecordingLogger(),
      new RetentionClock(options.Retention, new ReportingCalendar(options.TimeZone, options.FirstDayOfWeek)));

  [Test]
  public async Task AWriterWritesTheRowOfWorkersThatFlushedTogetherOnce()
  {
    using var opening = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    // The first write opens the partition and waits there, while two more workers flush.
    var options = TestEngine.Options(directory, o => o.Storage.Rollups.ConfigureMaintainer = _ =>
    {
      opening.Set();
      release.Wait(TimeSpan.FromSeconds(30));
    });
    using var rollups = new RollupStore(Path.Combine(directory, "rollups"), Granularity.Hour,
        options.Storage.Partitioning, [new RollupDefinition()], Factory(options), new RecordingLogger());
    using var pool = new RollupWriterPool(rollups, 1, new RecordingLogger());
    var bucket = Day0.AddHours(3).Ticks;
    var key = new RollupBufferKey(Granularity.Hour, bucket, "default", "purchase", 0, "");
    Task Flush(ulong visitor) => Task.Run(() => pool.Write(Rows(key, visitor), new Dictionary<RollupBufferKey, RollupAccumulator>[1], new RollupHandoff(1)));

    var first = Flush(1);
    Assert.That(opening.Wait(TimeSpan.FromSeconds(10)), Is.True);
    Task[] others = [Flush(2), Flush(3)];
    Assert.That(SpinWait.SpinUntil(() => pool.QueuedPieces == 2, TimeSpan.FromSeconds(10)), Is.True);
    release.Set();
    await Task.WhenAll([first, .. others]);

    var store = rollups.GetStore(Granularity.Hour);
    using var lease = store.Acquire(store.ListPartitions().Single());
    Assert.That(lease.Tree.TryGet(KeyCodec.RollupKey("default", "purchase", "", bucket, ""), out var stored), Is.True);
    Assert.Multiple(() =>
    {
      // The first flush alone, then the two queued flushes as one merged row.
      Assert.That(pool.RowsWritten, Is.EqualTo(2));
      Assert.That(stored.Count, Is.EqualTo(3));
      Assert.That(stored.Visitors.Estimate(), Is.EqualTo(3));
    });
  }

  [Test]
  public void WorkersOfAFailedBatchEachGetAnExceptionOfTheirOwn()
  {
    using var opening = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var opens = 0;
    // Every partition open fails; the first one waits there, while two more workers flush.
    var options = TestEngine.Options(directory, o => o.Storage.Rollups.ConfigureMaintainer = _ =>
    {
      if (Interlocked.Increment(ref opens) == 1)
      {
        opening.Set();
        release.Wait(TimeSpan.FromSeconds(30));
      }
      throw new IOException("Rollup storage unavailable");
    });
    using var rollups = new RollupStore(Path.Combine(directory, "rollups"), Granularity.Hour,
        options.Storage.Partitioning, [new RollupDefinition()], Factory(options), new RecordingLogger());
    using var pool = new RollupWriterPool(rollups, 1, new RecordingLogger());
    var key = new RollupBufferKey(Granularity.Hour, Day0.AddHours(3).Ticks, "default", "purchase", 0, "");
    Task Flush(ulong visitor) => Task.Run(() => pool.Write(Rows(key, visitor), new Dictionary<RollupBufferKey, RollupAccumulator>[1], new RollupHandoff(1)));

    var first = Flush(1);
    Assert.That(opening.Wait(TimeSpan.FromSeconds(10)), Is.True);
    Task[] others = [Flush(2), Flush(3)];
    Assert.That(SpinWait.SpinUntil(() => pool.QueuedPieces == 2, TimeSpan.FromSeconds(10)), Is.True);
    release.Set();
    Assert.ThrowsAsync<AnalyticsStorageException>(() => first);
    // The retry of the first batch fails once, for both queued flushes.
    var failures = others.Select(task => Assert.ThrowsAsync<AnalyticsStorageException>(() => task)).ToArray();
    Assert.Multiple(() =>
    {
      // Before: both workers rethrew the one failure, each rewriting its stack trace.
      Assert.That(failures[0], Is.Not.SameAs(failures[1]));
      Assert.That(failures[0].InnerException, Is.SameAs(failures[1].InnerException));
      Assert.That(failures[0].InnerException, Is.TypeOf<IOException>());
      Assert.That(failures[0].Message, Does.Contain("Rollup storage unavailable"));
    });
  }

  [Test]
  public void ManyWorkersAndWritersCountEveryEventOnce()
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Ingestion.WorkerCount = 8;
      o.Ingestion.RollupWriterCount = 3;
      // Small buffers make workers flush on their own, not only together.
      o.Ingestion.MaxBufferedRollupRows = 50;
    });
    for (var i = 0; i < 5_000; ++i)
    {
      Assert.That(engine.Track(new AnalyticsEvent(StandardEvents.PageView)
      {
        VisitorId = "v" + (i % 300), Url = "/p" + (i % 7), Timestamp = Utc(Day0.AddMinutes(i % 1_440))
      }), Is.True);
    }
    engine.Flush();
    AnalyticsQuery Query(QuerySource source) => new()
    {
      Range = Day(Day0), Source = source, GroupBy = { StandardDimensions.Page }, Metrics = { Metric.PageViews, Metric.Visitors }
    };
    var rollups = engine.Query(Query(QuerySource.Rollups));
    var raw = engine.Query(Query(QuerySource.RawEvents));
    Assert.Multiple(() =>
    {
      Assert.That(rollups.Totals[Metric.PageViews], Is.EqualTo(5_000));
      Assert.That(rollups.Totals[Metric.Visitors], Is.EqualTo(300));
      Assert.That(rollups.Rows.Select(row => (row[StandardDimensions.Page], row[Metric.PageViews], row[Metric.Visitors])),
          Is.EquivalentTo(raw.Rows.Select(row => (row[StandardDimensions.Page], row[Metric.PageViews], row[Metric.Visitors]))));
    });
  }

  [Test]
  public void FailuresOfSeveralWritersAreAllLogged()
  {
    var logger = new RecordingLogger();
    var storageUp = false;
    var engine = TestEngine.Create(directory, o =>
    {
      o.Ingestion.RollupWriterCount = 8;
      o.Storage.Logger = logger;
      // Every rollup partition open fails, so every writer with rows fails on its own.
      o.Storage.Rollups.ConfigureMaintainer = _ =>
      {
        if (!storageUp)
          throw new IOException("Rollup storage unavailable");
      };
    });
    try
    {
      for (var i = 0; i < 200; ++i)
        engine.Track(new AnalyticsEvent("purchase") { Site = "s" + i, Timestamp = Utc(Day0.AddMinutes(i)) });
      Assert.That(Assert.Throws<AnalyticsStorageException>(() => engine.Flush()).InnerException, Is.TypeOf<IOException>());
      var failures = logger.Problems.Count(problem => problem.Contains("Rollup storage unavailable", StringComparison.Ordinal));
      // Before: the worker reported the first writer's failure and dropped the others.
      Assert.That(failures, Is.GreaterThan(1));
    }
    finally
    {
      storageUp = true;
      engine.Dispose();
    }
  }

  [TestCase(0)]
  [TestCase(65)]
  public void RollupWriterCountIsValidated(int count)
  {
    var options = TestEngine.Options(directory, o => o.Ingestion.RollupWriterCount = count);
    var error = Assert.Throws<ArgumentException>(() => new AnalyticsEngine(options).Dispose());
    Assert.That(error.Message, Does.Contain("RollupWriterCount"));
  }
}
