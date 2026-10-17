using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Privacy;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class FlushRecoveryTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-2);

  /// <summary>Asserts that writing rollups fails with a storage failure of the given type.</summary>
  static void AssertRollupWriteFails<TFailure>(Func<Task> code) where TFailure : Exception =>
      Assert.That(Assert.ThrowsAsync<AnalyticsStorageException>(code).InnerException, Is.TypeOf<TFailure>());

  static RollupValue Value(ulong visitor, double amount)
  {
    var accumulator = new RollupAccumulator { Count = 1 };
    accumulator.AddVisitor(visitor, 14);
    accumulator.AddValue("amount", amount);
    return accumulator.ToValue();
  }

  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  [TestCase(true, true)]
  public void RowRetryDoesNotRepeatAnAddOrUpdate(bool failAfterWrite, bool existingRow)
  {
    var options = TestEngine.Options(directory);
    var factory = Factory(options);
    using var tree = factory.OpenRollups(Path.Combine(directory, "rollups"), Granularity.Hour);
    using var maintainer = factory.CreateRollupsMaintainer(tree);
    var key = KeyCodec.RollupKey("default", "purchase", "", Day0.Ticks, "");
    if (existingRow)
      tree.Upsert(key, Value(1, 10));
    var fault = new FaultingZoneTree<Memory<byte>, RollupValue>(tree);
    fault.FailOnUpsert = 1;
    fault.FailAfterWrite = failAfterWrite;
    var writer = new RollupWriter(fault);
    var row = new RollupWrite(key, Value(2, 20));
    Assert.Throws<IOException>(() => writer.Write(row));
    // A retry must also survive serialization/compaction of a value already visible in memory.
    maintainer.EvictToDisk();
    maintainer.WaitForBackgroundThreads();
    writer.Write(row);
    writer.Write(row);
    Assert.That(tree.TryGet(key, out var stored), Is.True);
    Assert.Multiple(() =>
    {
      Assert.That(stored.Count, Is.EqualTo(existingRow ? 2 : 1));
      Assert.That(stored.Visitors.Estimate(), Is.EqualTo(existingRow ? 2 : 1));
      Assert.That(stored.Values.Single().Sum, Is.EqualTo(existingRow ? 30 : 20));
      Assert.That(stored.Values.Single().Count, Is.EqualTo(existingRow ? 2 : 1));
      Assert.That(row.Completed, Is.True);
    });
  }

  [Test]
  public void WritesOfRowsInOtherLockStripesDoNotWait()
  {
    var options = TestEngine.Options(directory);
    using var tree = Factory(options).OpenRollups(Path.Combine(directory, "rollups"), Granularity.Hour);
    var first = KeyCodec.RollupKey("default", "purchase", "", Day0.Ticks, "");
    var second = Enumerable.Range(1, 1_000)
        .Select(hour => KeyCodec.RollupKey("default", "purchase", "", Day0.AddHours(hour).Ticks, ""))
        .First(key => RollupWriter.StripeOf(key.Span) != RollupWriter.StripeOf(first.Span));
    using var copying = new ManualResetEventSlim();
    using var secondWritten = new ManualResetEventSlim();
    // The copy of the first row runs under its lock and waits for the second row's write.
    var writer = new RollupWriter(tree, copy: (key, _) =>
    {
      if (!key.Span.SequenceEqual(first.Span))
        return;
      copying.Set();
      secondWritten.Wait(TimeSpan.FromSeconds(30));
    });
    var blocked = Task.Run(() => writer.Write(new RollupWrite(first, Value(1, 1))));
    Assert.That(copying.Wait(TimeSpan.FromSeconds(10)), Is.True);
    var other = Task.Run(() => writer.Write(new RollupWrite(second, Value(2, 2))));
    // Before: one lock per tree made the second write wait for the first copy.
    var finished = other.Wait(TimeSpan.FromSeconds(5));
    secondWritten.Set();
    blocked.Wait();
    other.Wait();
    Assert.That(finished, Is.True, "the second row waited for the first row's lock");
  }

  [Test]
  public async Task ConcurrentWritersOfTheSameRowsAddEveryDelta()
  {
    var options = TestEngine.Options(directory);
    using var tree = Factory(options).OpenRollups(Path.Combine(directory, "rollups"), Granularity.Hour);
    var writer = new RollupWriter(tree);
    var keys = Enumerable.Range(0, 50).Select(hour => KeyCodec.RollupKey("default", "purchase", "", Day0.AddHours(hour).Ticks, "")).ToArray();
    const int Writers = 16, WritesPerWriter = 1_000;
    await Task.WhenAll(Enumerable.Range(0, Writers).Select(worker => Task.Run(() =>
    {
      for (var i = 0; i < WritesPerWriter; ++i)
        writer.Write(new RollupWrite(keys[(worker + i) % keys.Length], Value((ulong)(worker * WritesPerWriter + i + 1), 1)));
    })));
    var total = 0L;
    foreach (var key in keys)
    {
      Assert.That(tree.TryGet(key, out var stored), Is.True);
      Assert.That(stored.Values.Single().Sum, Is.EqualTo(stored.Count));
      total += stored.Count;
    }
    Assert.That(total, Is.EqualTo(Writers * WritesPerWriter));
  }

  [Test]
  public async Task OtherWorkersFinishTheFailedReplacementBeforeAddingTheirDeltas()
  {
    var options = TestEngine.Options(directory);
    using var tree = Factory(options).OpenRollups(Path.Combine(directory, "rollups"), Granularity.Hour);
    var fault = new FaultingZoneTree<Memory<byte>, RollupValue>(tree);
    fault.FailOnUpsert = 1;
    fault.FailAfterWrite = true;
    var writer = new RollupWriter(fault);
    var key = KeyCodec.RollupKey("default", "purchase", "", Day0.Ticks, "");
    var failed = new RollupWrite(key, Value(1, 10));
    Assert.Throws<IOException>(() => writer.Write(failed));
    await Task.WhenAll(Enumerable.Range(2, 20).Select(visitor => Task.Run(() =>
        writer.Write(new RollupWrite(key, Value((ulong)visitor, 1))))));
    writer.Write(failed);
    Assert.That(tree.TryGet(key, out var stored), Is.True);
    Assert.Multiple(() =>
    {
      Assert.That(stored.Count, Is.EqualTo(21));
      Assert.That(stored.Visitors.Estimate(), Is.EqualTo(21));
      Assert.That(stored.Values.Single().Sum, Is.EqualTo(30));
    });
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  public async Task PartialTierFlushRecoversSessionDeltasAndNewEvents(int failOnOpen)
  {
    var opens = 0;
    void Configure(AnalyticsOptions options)
    {
      options.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
      options.Storage.Logger = new RecordingLogger();
      options.Storage.Rollups.ConfigureMaintainer = _ =>
      {
        if (Interlocked.Increment(ref opens) == failOnOpen)
          throw new IOException("Injected partition-open failure");
      };
    }
    using (var engine = TestEngine.Create(directory, Configure))
    {
      Track(engine, "/entry", 0, 10);
      AssertRollupWriteFails<IOException>(() => engine.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)));
      Assert.That(engine.GetStatistics().Flushes, Is.Zero);
      Track(engine, "/exit", 1, 20);
      await engine.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
      await engine.CompactAsync();
      foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups, QuerySource.Auto })
      {
        foreach (var interval in new[] { TimeInterval.Minute, TimeInterval.Hour, TimeInterval.Day })
          AssertSessionMetrics(engine, source, interval, 2, 60, 30);
      }
      var pages = engine.Query(new AnalyticsQuery
      {
        Range = Day(Day0), Source = QuerySource.Rollups, GroupBy = { StandardDimensions.Page },
        Metrics = { Metric.PageViews, Metric.Entrances, Metric.Exits }
      });
      Assert.Multiple(() =>
      {
        Assert.That(pages.Rows.Single(x => x.Dimensions[0] == "/entry")[Metric.Exits], Is.Zero);
        Assert.That(pages.Rows.Single(x => x.Dimensions[0] == "/exit")[Metric.Exits], Is.EqualTo(1));
      });
    }
    using var reopened = TestEngine.Create(directory, Configure);
    Track(reopened, "/next", 2, 40);
    await reopened.FlushAsync();
    AssertSessionMetrics(reopened, QuerySource.Rollups, TimeInterval.Day, 3, 120, 70);
  }

  static void Track(AnalyticsEngine engine, string page, int minute, double amount) =>
      engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = "alice", Url = page, Timestamp = Utc(Day0.AddMinutes(minute)), Values = { ["amount"] = amount }
      });

  static void AssertSessionMetrics(AnalyticsEngine engine, QuerySource source, TimeInterval interval,
      int views, double duration, double amount)
  {
    var row = engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Source = source, Interval = interval,
      Metrics = { Metric.PageViews, Metric.Visitors, Metric.Sessions, Metric.Bounces, Metric.SessionDuration,
          Metric.Entrances, Metric.Exits, Metric.Sum("amount") }
    }).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(row[Metric.PageViews], Is.EqualTo(views));
      Assert.That(row[Metric.Visitors], Is.EqualTo(1));
      Assert.That(row[Metric.Sessions], Is.EqualTo(1));
      Assert.That(row[Metric.Bounces], Is.Zero);
      Assert.That(row[Metric.SessionDuration], Is.EqualTo(duration));
      Assert.That(row[Metric.Entrances], Is.EqualTo(1));
      Assert.That(row[Metric.Exits], Is.EqualTo(1));
      Assert.That(row[Metric.Sum("amount")], Is.EqualTo(amount));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task SessionPersistenceFailureDoesNotReplaySuccessfulRollups(bool failAfterWrite)
  {
    await using (var harness = new WorkerHarness(directory))
    {
      harness.SessionFault.FailOnUpsert = 2;
      harness.SessionFault.FailAfterWrite = failAfterWrite;
      await harness.TrackAsync("alice", "/entry", 0);
      await harness.TrackAsync("bob", "/other", 0);
      Assert.ThrowsAsync<IOException>(() => harness.FlushAsync());
      await harness.TrackAsync("alice", "/exit", 1);
      await harness.FlushAsync();
      await harness.FlushAsync();
      Assert.Multiple(() =>
      {
        Assert.That(harness.Counters.Errors, Is.EqualTo(1));
        Assert.That(harness.Counters.Flushes, Is.EqualTo(2));
      });
    }
    using var reopened = TestEngine.Create(directory, options => options.Storage.StoreRawEvents = false);
    Track(reopened, "/next", 2, 0);
    await reopened.FlushAsync();
    var totals = reopened.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Metrics = { Metric.PageViews, Metric.Sessions, Metric.Bounces, Metric.SessionDuration }
    }).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(totals[Metric.PageViews], Is.EqualTo(4));
      Assert.That(totals[Metric.Sessions], Is.EqualTo(2));
      Assert.That(totals[Metric.Bounces], Is.EqualTo(1));
      Assert.That(totals[Metric.SessionDuration], Is.EqualTo(120));
    });
  }

  [Test]
  public async Task RepeatedFailuresKeepThePartitionLeasedUntilRecovery()
  {
    using var engine = TestEngine.Create(directory, options =>
    {
      options.Sessions.Enabled = false;
      options.Storage.StoreRawEvents = false;
      options.Rollups.Granularities = Granularity.Hour;
      options.Rollups.Definitions.Clear();
      options.Rollups.Definitions.Add(new RollupDefinition());
      options.Storage.Logger = new RecordingLogger();
    });
    Track(engine, "/entry", 0, 10);
    await engine.FlushAsync();
    var store = engine.RollupStore.GetStore(Granularity.Hour);
    IZoneTree<Memory<byte>, RollupValue> tree;
    using (var lease = store.Acquire(store.ListPartitions().Single()))
      tree = lease.Tree;
    tree.IsReadOnly = true;
    try
    {
      Track(engine, "/exit", 1, 20);
      AssertRollupWriteFails<ZoneTree.Exceptions.ZoneTreeIsReadOnlyException>(() => engine.FlushAsync());
      AssertRollupWriteFails<ZoneTree.Exceptions.ZoneTreeIsReadOnlyException>(() => engine.FlushAsync());
      store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
      Assert.Multiple(() =>
      {
        Assert.That(store.OpenPartitionCount, Is.EqualTo(1), "the retry still owns this tree");
        Assert.That(engine.GetStatistics().Flushes, Is.EqualTo(1));
        Assert.That(engine.GetStatistics().Errors, Is.EqualTo(2));
      });
    }
    finally
    {
      tree.IsReadOnly = false;
    }
    await engine.FlushAsync();
    store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    Assert.That(store.OpenPartitionCount, Is.Zero, "successful recovery releases the lease");
    var totals = engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Metrics = { Metric.PageViews, Metric.Sum("amount") }
    }).Totals;
    Assert.Multiple(() =>
    {
      Assert.That(totals[Metric.PageViews], Is.EqualTo(2));
      Assert.That(totals[Metric.Sum("amount")], Is.EqualTo(30));
    });
  }

  [Test]
  public async Task AutomaticFlushRetriesAnIdleBatchAfterAStorageFailure()
  {
    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var opens = 0;
    using var engine = TestEngine.Create(directory, options =>
    {
      options.Ingestion.FlushInterval = TimeSpan.FromMilliseconds(20);
      options.Sessions.Enabled = false;
      options.Storage.StoreRawEvents = false;
      options.Rollups.Granularities = Granularity.Hour;
      options.Rollups.Definitions.Clear();
      options.Rollups.Definitions.Add(new RollupDefinition());
      options.Storage.Logger = new RecordingLogger();
      options.Storage.Rollups.ConfigureMaintainer = _ =>
      {
        if (Interlocked.Increment(ref opens) == 1)
          throw new IOException("Injected automatic flush failure");
        opened.TrySetResult();
      };
    });
    Track(engine, "/entry", 0, 10);
    await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // Queued after the automatic retry so it observes that retry's completed writes.
    await engine.FlushAsync();
    Assert.Multiple(() =>
    {
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(1));
      Assert.That(engine.Query(new AnalyticsQuery
      {
        Range = Day(Day0), Source = QuerySource.Rollups, Metrics = { Metric.PageViews, Metric.Sum("amount") }
      }).Totals[Metric.PageViews], Is.EqualTo(1));
    });
  }

  [Test]
  public void FinalFlushFailureIsReportedAfterReleasingStorage()
  {
    var unavailable = true;
    var engine = TestEngine.Create(directory, options =>
    {
      options.Storage.Logger = new RecordingLogger();
      options.Storage.Rollups.ConfigureMaintainer = _ =>
      {
        if (unavailable)
          throw new IOException("Storage unavailable at shutdown");
      };
    });
    Track(engine, "/entry", 0, 0);
    AssertRollupWriteFails<IOException>(() => engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    unavailable = false;
    using var reopened = TestEngine.Create(directory);
    Assert.That(reopened.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Source = QuerySource.RawEvents, Metrics = { Metric.PageViews }
    }).Totals[Metric.PageViews], Is.EqualTo(1));
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task PersistentFailureUsesBackpressureAndShutdownWakesTheRetryWait(bool shutdown)
  {
    var attempts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var unavailable = true;
    var opens = 0;
    var harness = new WorkerHarness(directory, options =>
    {
      options.Sessions.Enabled = false;
      options.Ingestion.MaxBufferedRollupRows = 1;
      options.Ingestion.QueueCapacity = 8;
      // One writer opens one partition per flush attempt, so two opens are two attempts.
      options.Ingestion.RollupWriterCount = 1;
      // A long delay makes it clear that shutdown wakes the retry wait immediately.
      options.Storage.Rollups.ConfigureMaintainer = _ =>
      {
        if (unavailable)
        {
          if (Interlocked.Increment(ref opens) == 2)
            attempts.TrySetResult();
          throw new IOException("Storage unavailable");
        }
      };
    });
    try
    {
      for (var i = 0; i < 10; ++i)
        await harness.TrackAsync("v" + i, "/", 0);
      await attempts.Task.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.Multiple(() =>
      {
        Assert.That(Interlocked.Read(ref harness.Counters.EventsProcessed), Is.EqualTo(2));
        Assert.That(harness.Worker.QueueLength, Is.EqualTo(8));
      });
      if (shutdown)
      {
        AssertRollupWriteFails<IOException>(() => harness.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
      }
      else
      {
        unavailable = false;
        // Graceful disposal wakes the wait, drains the queue, and retries both retained buffers.
        await harness.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var reopened = TestEngine.Create(directory, options =>
        {
          options.Sessions.Enabled = false;
          options.Storage.StoreRawEvents = false;
        });
        Assert.That(reopened.Query(new AnalyticsQuery
        {
          Range = Day(Day0), Metrics = { Metric.PageViews }
        }).Totals[Metric.PageViews], Is.EqualTo(10));
      }
    }
    finally
    {
      unavailable = false;
      await harness.DisposeAsync();
    }
  }

  static StoreFactory Factory(AnalyticsOptions options) => new(options.Storage,
      Path.GetFullPath(options.DataDirectory), new RecordingLogger(),
      new RetentionClock(options.Retention, new ReportingCalendar(options.TimeZone, options.FirstDayOfWeek)));

  sealed class WorkerHarness : IAsyncDisposable
  {
    readonly RollupStore rollups;
    readonly RollupWriterPool rollupWriters;
    readonly IZoneTree<ulong, SessionState> sessions;
    readonly IMaintainer maintainer;
    bool disposed;

    public readonly IngestionWorker Worker;
    public readonly Counters Counters = new();
    public readonly FaultingZoneTree<ulong, SessionState> SessionFault;

    public WorkerHarness(string directory, Action<AnalyticsOptions> configure = null)
    {
      var options = TestEngine.Options(directory, o =>
      {
        o.Storage.StoreRawEvents = false;
        configure?.Invoke(o);
      });
      // An engine creates the directory's metadata, so engines can reopen what the worker wrote.
      using (TestEngine.Create(directory, o => o.Storage.StoreRawEvents = false)) { }
      var calendar = new ReportingCalendar(options.TimeZone, options.FirstDayOfWeek);
      var meta = new MetaStore(Path.Combine(directory, "meta"));
      var hasher = new VisitorHasher(options.Privacy.VisitorIdentification, null, meta);
      var logger = new RecordingLogger();
      var enricher = new EnrichmentPipeline(options, hasher, logger, Counters);
      var factory = Factory(options);
      if (options.Sessions.Enabled)
      {
        sessions = factory.OpenSessions(Path.Combine(directory, "sessions"), enricher.Schema);
        maintainer = factory.CreateSessionsMaintainer(sessions);
        SessionFault = new(sessions);
      }
      var definitions = options.Rollups.Definitions.ToArray();
      rollups = new RollupStore(Path.Combine(directory, "rollups"), options.Rollups.Granularities,
          options.Storage.Partitioning, definitions, factory, logger);
      rollupWriters = new RollupWriterPool(rollups, options.Ingestion.RollupWriterCount, logger);
      Worker = new IngestionWorker(new IngestionServices
      {
        Options = options, Calendar = calendar, Enricher = enricher, Sessions = SessionFault,
        Rollups = rollups, RollupWriters = rollupWriters, Definitions = definitions, Logger = logger, Counters = Counters
      });
      Worker.Start();
    }

    public async Task TrackAsync(string visitor, string page, int minute) =>
        await Worker.Writer.WriteAsync(new WorkItem(new AnalyticsEvent("pageview")
        {
          VisitorId = visitor, Url = page, Timestamp = Utc(Day0.AddMinutes(minute))
        }.Snapshot(DateTime.UtcNow.Ticks)));

    public async Task FlushAsync()
    {
      var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      await Worker.Writer.WriteAsync(new WorkItem(completion));
      await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
      if (disposed)
        return;
      disposed = true;
      try
      {
        await Worker.DisposeAsync();
      }
      finally
      {
        rollupWriters.Dispose();
        rollups.Dispose();
        maintainer?.Dispose();
        sessions?.Dispose();
      }
    }
  }
}

