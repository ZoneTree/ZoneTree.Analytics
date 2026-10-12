using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class PartitionLifecycleTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-5);

  [TestCase("Evict")]
  [TestCase("Wait")]
  public void FailedCompactionKeepsTheSameUsableTree(string phase)
  {
    using var harness = new StoreHarness(directory);
    var name = harness.Write(Day0);
    var tree = harness.Trees.Single();
    var maintainer = harness.Maintainers.Single();
    maintainer.BeforeOperation = operation =>
    {
      if (operation == phase)
        throw new IOException("Injected compaction failure");
    };
    harness.Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    using (var lease = harness.Store.Acquire(name))
    {
      Assert.Multiple(() =>
      {
        Assert.That(lease.Tree, Is.SameAs(tree));
        Assert.That(harness.Trees, Has.Count.EqualTo(1));
        Assert.That(tree.Released, Is.False);
        Assert.That(maintainer.DisposeAttempts, Is.Zero);
      });
    }
    // Shutdown still owns and closes the failed idle partition.
    harness.Store.Dispose();
    Assert.That(tree.Released, Is.True);
  }

  [TestCase("Maintainer")]
  [TestCase("Tree")]
  public void PartialDisposalBlocksReopenAndResumesAtTheFailedResource(string phase)
  {
    using var harness = new StoreHarness(directory);
    var name = harness.Write(Day0);
    var tree = harness.Trees.Single();
    var maintainer = harness.Maintainers.Single();
    var unavailable = true;
    maintainer.BeforeOperation = operation =>
    {
      if (phase == "Maintainer" && operation == "Dispose" && unavailable)
        throw new IOException("Injected maintainer disposal failure");
    };
    tree.BeforeDispose = () =>
    {
      if (phase == "Tree" && unavailable)
        throw new IOException("Injected tree disposal failure");
    };
    harness.Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    var error = Assert.Throws<IOException>(() => harness.Store.Acquire(name));
    Assert.Multiple(() =>
    {
      Assert.That(error.InnerException, Is.TypeOf<IOException>());
      Assert.That(harness.Trees, Has.Count.EqualTo(1));
      Assert.That(harness.Store.OpenPartitionCount, Is.EqualTo(1));
      Assert.That(harness.Store.Describe().Single().ApproximateRecordCount, Is.EqualTo(1));
    });
    unavailable = false;
    // Failed teardown is retried even when the normal idle policy would keep this tree open.
    harness.Store.CloseIdle(TimeSpan.FromDays(1), keepLatestOpen: true);
    Assert.Multiple(() =>
    {
      Assert.That(tree.Released, Is.True);
      Assert.That(maintainer.DisposeAttempts, Is.EqualTo(phase == "Maintainer" ? 2 : 1));
      Assert.That(tree.DisposeAttempts, Is.EqualTo(phase == "Tree" ? 2 : 1));
    });
    using var reopened = harness.Store.Acquire(name);
    Assert.That(reopened.Tree.TryGet(KeyCodec.RawKey("s", Day0.Ticks, 1), out var value), Is.True);
    Assert.That(value.ToArray(), Is.EqualTo(new byte[] { 1 }));
    Assert.That(harness.Trees, Has.Count.EqualTo(2));
  }

  [Test]
  public void ShutdownAttemptsOtherPartitionsAndRetainsTheFailedOneForRetry()
  {
    using var harness = new StoreHarness(directory);
    harness.Write(Day0);
    Assert.That(harness.Store.RollOver(Day0.AddDays(1).Ticks), Is.True);
    harness.Write(Day0.AddDays(2));
    var failed = harness.Trees[0];
    failed.BeforeDispose = () => throw new IOException("Injected shutdown failure");
    Assert.Throws<IOException>(() => harness.Store.Dispose());
    Assert.Multiple(() =>
    {
      Assert.That(failed.Released, Is.False);
      Assert.That(harness.Trees[1].Released, Is.True);
      Assert.That(harness.Store.OpenPartitionCount, Is.EqualTo(1));
      Assert.That(harness.Maintainers.Select(x => x.DisposeAttempts), Is.All.EqualTo(1));
    });
    failed.BeforeDispose = null;
    harness.Store.Dispose();
    Assert.Multiple(() =>
    {
      Assert.That(failed.Released, Is.True);
      Assert.That(harness.Trees[1].DisposeAttempts, Is.EqualTo(1));
      Assert.That(harness.Maintainers.Select(x => x.DisposeAttempts), Is.All.EqualTo(1));
    });
  }

  [Test]
  public void ShutdownReportsEveryCleanupFailureAndRetriesOnlyRemainingResources()
  {
    using var harness = new StoreHarness(directory);
    harness.Write(Day0);
    Assert.That(harness.Store.RollOver(Day0.AddDays(1).Ticks), Is.True);
    harness.Write(Day0.AddDays(2));
    foreach (var tree in harness.Trees)
      tree.BeforeDispose = () => throw new IOException("Injected independent cleanup failure");
    var error = Assert.Throws<AggregateException>(() => harness.Store.Dispose());
    Assert.Multiple(() =>
    {
      Assert.That(error.InnerExceptions, Has.Count.EqualTo(2));
      Assert.That(harness.Store.OpenPartitionCount, Is.EqualTo(2));
      Assert.That(harness.Maintainers.Select(x => x.DisposeAttempts), Is.All.EqualTo(1));
    });
    foreach (var tree in harness.Trees)
      tree.BeforeDispose = null;
    harness.Store.Dispose();
    Assert.Multiple(() =>
    {
      Assert.That(harness.Trees.Select(x => x.Released), Is.All.True);
      Assert.That(harness.Maintainers.Select(x => x.DisposeAttempts), Is.All.EqualTo(1));
    });
  }

  [Test]
  public void FailedMaintainerCreationAndTreeCleanupRemainOwned()
  {
    using var harness = new StoreHarness(directory);
    var name = harness.Store.Route(Day0.Ticks);
    harness.OnOpen = tree => tree.BeforeDispose = () => throw new IOException("Injected tree cleanup failure");
    harness.BeforeMaintainer = _ => throw new IOException("Injected maintainer creation failure");
    var error = Assert.Throws<AggregateException>(() => harness.Store.Acquire(name));
    Assert.That(error.InnerExceptions, Has.Count.EqualTo(2));
    Assert.Throws<IOException>(() => harness.Store.Acquire(name));
    Assert.That(harness.Trees, Has.Count.EqualTo(1));
    harness.Trees[0].BeforeDispose = null;
    harness.BeforeMaintainer = null;
    harness.Store.Dispose();
    Assert.That(harness.Trees[0].Released, Is.True);
  }

  [TestCase(false)]
  [TestCase(true)]
  public void FailedDropTeardownKeepsOwnershipUntilTheRetryCompletes(bool failInTree)
  {
    using var harness = new StoreHarness(directory);
    var old = harness.Write(Day0);
    Assert.That(harness.Store.RollOver(Day0.AddDays(1).Ticks), Is.True);
    harness.Write(Day0.AddDays(2));
    var tree = harness.Trees[0];
    var maintainer = harness.Maintainers[0];
    var unavailable = true;
    maintainer.BeforeOperation = phase =>
    {
      if (!failInTree && phase == "Cancel" && unavailable)
        throw new IOException("Injected drop cancellation failure");
    };
    tree.BeforeDispose = () =>
    {
      if (failInTree && unavailable)
        throw new IOException("Injected drop tree disposal failure");
    };
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.Zero);
    Assert.Multiple(() =>
    {
      Assert.That(tree.Released, Is.False);
      Assert.That(harness.Store.Acquire(old), Is.Null);
      Assert.That(harness.Store.OpenPartitionCount, Is.EqualTo(2));
    });
    unavailable = false;
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.EqualTo(1));
    Assert.Multiple(() =>
    {
      Assert.That(tree.Released, Is.True);
      Assert.That(harness.Store.OpenPartitionCount, Is.EqualTo(1));
      Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.Zero);
      Assert.That(maintainer.DisposeAttempts, Is.EqualTo(1));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void FailedDeletionIsRetriedAndCountedOnlyOnce(bool opened)
  {
    using var harness = new StoreHarness(directory);
    var old = harness.Write(Day0);
    Assert.That(harness.Store.RollOver(Day0.AddDays(1).Ticks), Is.True);
    harness.Write(Day0.AddDays(2));
    if (!opened)
      harness.Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    var unavailable = true;
    harness.BeforeDelete = _ =>
    {
      if (unavailable)
        throw new IOException("Injected directory deletion failure");
    };
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.Zero);
    Assert.Multiple(() =>
    {
      Assert.That(harness.Store.Acquire(old), Is.Null);
      Assert.That(harness.Store.ListPartitions(), Has.Count.EqualTo(1));
      Assert.That(harness.Logger.Problems, Is.Not.Empty);
    });
    unavailable = false;
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.EqualTo(1));
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.Zero);
    Assert.That(Directory.Exists(Path.Combine(harness.Store.RootDirectory, old)), Is.False);
  }

  [Test]
  public void FailureDeletingDataStillAttemptsTheSeparateWriteAheadLog()
  {
    using var harness = new StoreHarness(directory, separateWal: true);
    var old = harness.Write(Day0);
    Assert.That(harness.Store.RollOver(Day0.AddDays(1).Ticks), Is.True);
    harness.Write(Day0.AddDays(2));
    harness.Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    var path = Path.Combine(harness.Store.RootDirectory, old);
    var wal = harness.Factory.WriteAheadLogDirectoryOf(path);
    var unavailable = true;
    harness.BeforeDelete = deleting =>
    {
      if (deleting == path && unavailable)
        throw new IOException("Injected data directory deletion failure");
    };
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.Zero);
    Assert.Multiple(() =>
    {
      Assert.That(Directory.Exists(path), Is.True);
      Assert.That(Directory.Exists(wal), Is.False);
    });
    unavailable = false;
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.EqualTo(1));
    Assert.That(Directory.Exists(path), Is.False);
  }

  [Test]
  public void DeferredDropIsCountedAfterTheLastLeaseIsReleased()
  {
    using var harness = new StoreHarness(directory);
    var old = harness.Write(Day0);
    Assert.That(harness.Store.RollOver(Day0.AddDays(1).Ticks), Is.True);
    harness.Write(Day0.AddDays(2));
    using var lease = harness.Store.Acquire(old);
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.Zero);
    Assert.That(harness.Store.Acquire(old), Is.Null);
    Assert.That(harness.Trees[0].Released, Is.False);
    lease.Dispose();
    Assert.That(harness.Trees[0].Released, Is.True);
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.EqualTo(1));
    Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.Zero);
  }

  [Test]
  public async Task ConcurrentAcquireReusesTheTreeAfterCompactionFails()
  {
    using var harness = new StoreHarness(directory);
    var name = harness.Write(Day0);
    using var entered = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    harness.Maintainers[0].BeforeOperation = phase =>
    {
      if (phase == "Evict")
      {
        entered.Set();
        if (!resume.Wait(TimeSpan.FromSeconds(5)))
          throw new TimeoutException("Test did not release the close operation.");
        throw new IOException("Injected concurrent close failure");
      }
    };
    var closing = Task.Run(() => harness.Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false));
    Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
    var acquiring = Task.Run(() => harness.Store.Acquire(name));
    try
    {
      Assert.That(acquiring.IsCompleted, Is.False);
    }
    finally
    {
      resume.Set();
    }
    await closing.WaitAsync(TimeSpan.FromSeconds(5));
    using var lease = await acquiring.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.That(lease.Tree, Is.SameAs(harness.Trees.Single()));
  }

  [Test]
  public async Task ConcurrentDropEscalatesAnIdleCloseWithoutDuplicatingDeletion()
  {
    using var harness = new StoreHarness(directory);
    var name = harness.Write(Day0);
    using var entered = new ManualResetEventSlim();
    using var resume = new ManualResetEventSlim();
    harness.Maintainers[0].BeforeOperation = phase =>
    {
      if (phase == "Evict")
      {
        entered.Set();
        if (!resume.Wait(TimeSpan.FromSeconds(5)))
          throw new TimeoutException("Test did not release the close operation.");
      }
    };
    var closing = Task.Run(() => harness.Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false));
    Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
    var dropping = Task.Run(() => harness.Store.Drop(name));
    try
    {
      Assert.That(SpinWait.SpinUntil(() => harness.Store.ListPartitions().Count == 0, 5000), Is.True);
      Assert.That(harness.Store.Acquire(name), Is.Null);
    }
    finally
    {
      resume.Set();
    }
    await Task.WhenAll(closing, dropping).WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Multiple(() =>
    {
      Assert.That(harness.Store.DropPartitionsEndingBefore(long.MaxValue), Is.EqualTo(1));
      Assert.That(harness.Trees[0].DisposeAttempts, Is.EqualTo(1));
      Assert.That(Directory.Exists(Path.Combine(harness.Store.RootDirectory, name)), Is.False);
    });
  }

  [Test]
  public async Task FailedEngineCleanupKeepsTheDirectoryLockedUntilRetrySucceeds()
  {
    var engine = TestEngine.Create(directory);
    engine.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/", Timestamp = Utc(Day0) });
    await engine.FlushAsync();
    IZoneTree<Memory<byte>, Memory<byte>> tree;
    using (var lease = engine.RawEventStore.Store.Acquire(engine.RawEventStore.Store.ListPartitions().Single()))
      tree = lease.Tree;
    ZoneTreeIsDisposing<Memory<byte>, Memory<byte>> fail = _ => throw new IOException("Injected engine cleanup failure");
    tree.Maintenance.OnZoneTreeIsDisposing += fail;
    try
    {
      Assert.ThrowsAsync<IOException>(() => engine.DisposeAsync().AsTask());
      Assert.Throws<IOException>(() => TestEngine.Create(directory));
      Assert.Multiple(() =>
      {
        Assert.That(engine.RawEventStore.Store.OpenPartitionCount, Is.EqualTo(1));
        Assert.That(engine.RollupStore.AllStores.Sum(x => x.OpenPartitionCount), Is.Zero);
      });
    }
    finally
    {
      tree.Maintenance.OnZoneTreeIsDisposing -= fail;
      await engine.DisposeAsync();
    }
    using var reopened = TestEngine.Create(directory);
    Assert.That(reopened.Query(new AnalyticsQuery { Range = Day(Day0), Metrics = { Metric.PageViews } })
        .Totals[Metric.PageViews], Is.EqualTo(1));
  }

  [Test]
  public async Task ConcurrentEngineDisposalsWaitForTheSameCleanup()
  {
    var engine = TestEngine.Create(directory);
    engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(Day0) });
    await engine.FlushAsync();
    IZoneTree<Memory<byte>, Memory<byte>> tree;
    using (var lease = engine.RawEventStore.Store.Acquire(engine.RawEventStore.Store.ListPartitions().Single()))
      tree = lease.Tree;
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var resume = new ManualResetEventSlim();
    tree.Maintenance.OnZoneTreeIsDisposing += _ =>
    {
      entered.TrySetResult();
      if (!resume.Wait(TimeSpan.FromSeconds(5)))
        throw new TimeoutException("Test did not release engine cleanup.");
    };
    var first = engine.DisposeAsync().AsTask();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var second = engine.DisposeAsync().AsTask();
    try
    {
      Assert.That(second, Is.SameAs(first));
      Assert.That(second.IsCompleted, Is.False);
    }
    finally
    {
      resume.Set();
    }
    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    using var reopened = TestEngine.Create(directory);
  }

  [Test]
  public async Task CleanupRetriesAreAsynchronousAndSharedByConcurrentCallers()
  {
    var engine = TestEngine.Create(directory);
    engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(Day0) });
    await engine.FlushAsync();
    IZoneTree<Memory<byte>, Memory<byte>> tree;
    using (var lease = engine.RawEventStore.Store.Acquire(engine.RawEventStore.Store.ListPartitions().Single()))
      tree = lease.Tree;
    ZoneTreeIsDisposing<Memory<byte>, Memory<byte>> fail = _ => throw new IOException("Injected first cleanup failure");
    tree.Maintenance.OnZoneTreeIsDisposing += fail;
    Assert.ThrowsAsync<IOException>(() => engine.DisposeAsync().AsTask());
    tree.Maintenance.OnZoneTreeIsDisposing -= fail;
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var resume = new ManualResetEventSlim();
    ZoneTreeIsDisposing<Memory<byte>, Memory<byte>> block = _ =>
    {
      entered.TrySetResult();
      if (!resume.Wait(TimeSpan.FromSeconds(10)))
        throw new TimeoutException("Test did not release cleanup retry.");
    };
    tree.Maintenance.OnZoneTreeIsDisposing += block;
    var invocation = Task.Run(() => returned.TrySetResult(engine.DisposeAsync().AsTask()));
    try
    {
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
      var retry = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
      var concurrent = engine.DisposeAsync().AsTask();
      Assert.That(concurrent, Is.SameAs(retry));
      Assert.That(retry.IsCompleted, Is.False);
    }
    finally
    {
      resume.Set();
      await invocation;
      tree.Maintenance.OnZoneTreeIsDisposing -= block;
      await engine.DisposeAsync();
    }
    using var reopened = TestEngine.Create(directory);
  }

  sealed class StoreHarness : IDisposable
  {
    public readonly StoreFactory Factory;
    public readonly RecordingLogger Logger = new();
    public readonly List<FaultingZoneTree<Memory<byte>, Memory<byte>>> Trees = [];
    public readonly List<FaultingMaintainer> Maintainers = [];
    public readonly PartitionedStore<Memory<byte>, Memory<byte>> Store;
    public Action<FaultingZoneTree<Memory<byte>, Memory<byte>>> OnOpen;
    public Action<IZoneTree<Memory<byte>, Memory<byte>>> BeforeMaintainer;
    public Action<string> BeforeDelete;

    public StoreHarness(string directory, bool separateWal = false)
    {
      var options = TestEngine.Options(directory);
      if (separateWal)
        options.Storage.WriteAheadLogDirectory = Path.Combine(directory, "wal");
      Factory = new StoreFactory(options.Storage, Path.GetFullPath(directory), Logger,
          new RetentionClock(options.Retention, new ReportingCalendar(options.TimeZone, options.FirstDayOfWeek)));
      Store = new(Path.Combine(directory, "events"), 1,
          path =>
          {
            var tree = new FaultingZoneTree<Memory<byte>, Memory<byte>>(Factory.OpenRawEvents(path));
            Trees.Add(tree);
            OnOpen?.Invoke(tree);
            return tree;
          }, Logger,
          tree =>
          {
            BeforeMaintainer?.Invoke(tree);
            var maintainer = new FaultingMaintainer(Factory.CreateRawEventsMaintainer(tree));
            Maintainers.Add(maintainer);
            return maintainer;
          }, Factory.WriteAheadLogDirectoryOf,
          path =>
          {
            BeforeDelete?.Invoke(path);
            if (Directory.Exists(path))
              Directory.Delete(path, recursive: true);
          });
    }

    public string Write(DateTime time)
    {
      var name = Store.Route(time.Ticks);
      using var lease = Store.Acquire(name);
      lease.Tree.Upsert(KeyCodec.RawKey("s", time.Ticks, 1), new byte[] { 1 });
      return name;
    }

    public void Dispose() => Store.Dispose();
  }

  sealed class FaultingMaintainer(IMaintainer inner) : IMaintainer
  {
    public Action<string> BeforeOperation;
    public int DisposeAttempts;
    public void Dispose() { ++DisposeAttempts; BeforeOperation?.Invoke("Dispose"); inner.Dispose(); }
    public void EvictToDisk() { BeforeOperation?.Invoke("Evict"); inner.EvictToDisk(); }
    public void WaitForBackgroundThreads() { BeforeOperation?.Invoke("Wait"); inner.WaitForBackgroundThreads(); }
    public void TryCancelBackgroundThreads() { BeforeOperation?.Invoke("Cancel"); inner.TryCancelBackgroundThreads(); }
    public Task WaitForBackgroundThreadsAsync() => inner.WaitForBackgroundThreadsAsync();
    public void StartMerge() => inner.StartMerge();
    public void StartBottomSegmentsMerge(int fromIndex = 0, int toIndex = int.MaxValue) => inner.StartBottomSegmentsMerge(fromIndex, toIndex);
    public int ThresholdForMergeOperationStart { get => inner.ThresholdForMergeOperationStart; set => inner.ThresholdForMergeOperationStart = value; }
    public int MaximumReadOnlySegmentCount { get => inner.MaximumReadOnlySegmentCount; set => inner.MaximumReadOnlySegmentCount = value; }
    public bool EnableJobForCleaningInactiveCaches { get => inner.EnableJobForCleaningInactiveCaches; set => inner.EnableJobForCleaningInactiveCaches = value; }
    public TimeSpan BlockCacheLifeTime { get => inner.BlockCacheLifeTime; set => inner.BlockCacheLifeTime = value; }
    public TimeSpan InactiveBlockCacheCleanupInterval { get => inner.InactiveBlockCacheCleanupInterval; set => inner.InactiveBlockCacheCleanupInterval = value; }
  }
}
