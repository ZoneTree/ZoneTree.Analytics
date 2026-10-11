using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class AdvancedStorageTests : DirectoryTest
{
  static readonly DateTime First = DateTime.UtcNow.Date.AddDays(-10).AddHours(10);

  static void Track(AnalyticsEngine engine, DateTime time)
  {
    engine.Track(new AnalyticsEvent(StandardEvents.PageView)
    {
      Url = "/advanced", VisitorId = "visitor", Timestamp = Utc(time),
      Values = { ["duration"] = 42 }
    });
    engine.Flush();
  }

  [Test]
  public void AccessUsesTheActualTreesAndConfiguredMaintainers()
  {
    var rawMaintainers = new List<IMaintainer>();
    var rollupMaintainers = new List<IMaintainer>();
    IMaintainer sessionMaintainer = null;
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
      o.Storage.RawEvents.ConfigureMaintainer = rawMaintainers.Add;
      o.Storage.Rollups.ConfigureMaintainer = rollupMaintainers.Add;
      o.Storage.Sessions.ConfigureMaintainer = m => sessionMaintainer = m;
    });
    Track(engine, First);
    var raw = engine.Storage.RawEvents;
    using var lease = raw.Acquire(raw.GetPartitions().Single());
    using var internalLease = engine.RawEventStore.Store.Acquire(lease.PartitionName);
    Assert.Multiple(() =>
    {
      Assert.That(lease.ZoneTree, Is.SameAs(internalLease.Tree));
      Assert.That(lease.Maintainer, Is.SameAs(rawMaintainers.Single()));
      Assert.That(engine.Storage.SessionsMaintainer, Is.SameAs(sessionMaintainer));
      Assert.That(raw.Directory, Is.EqualTo(Path.Combine(Path.GetFullPath(directory), "events")));
    });
    using var events = lease.ZoneTree.CreateIterator();
    Assert.That(events.Next(), Is.True);
    Assert.That(events.CurrentValue.Length, Is.GreaterThan(0));
    foreach (var granularity in RollupStore.Granularities)
    {
      var store = engine.Storage.GetRollups(granularity);
      using var partition = store.Acquire(store.GetPartitions().Single());
      using var expected = engine.RollupStore.GetStore(granularity).Acquire(partition.PartitionName);
      Assert.That(partition.ZoneTree, Is.SameAs(expected.Tree));
      Assert.That(rollupMaintainers, Does.Contain(partition.Maintainer));
      using var rows = partition.ZoneTree.CreateIterator();
      Assert.That(rows.Next(), Is.True);
      var row = rows.CurrentValue;
      Assert.That(row.Count, Is.GreaterThan(0));
      var copy = partition.ZoneTree.ValueSerializer.Deserialize(partition.ZoneTree.ValueSerializer.Serialize(row));
      Assert.That(copy.Count, Is.EqualTo(row.Count));
      partition.Maintainer.EvictToDisk();
      partition.Maintainer.WaitForBackgroundThreads();
    }
    using var sessions = engine.Storage.Sessions.CreateIterator();
    Assert.That(sessions.Next(), Is.True);
    Assert.That(sessions.CurrentValue.PageViews, Is.EqualTo(1));
    Assert.That(sessions.CurrentValue.Clone().Dimensions, Is.Not.SameAs(sessions.CurrentValue.Dimensions));
    lease.Maintainer.ThresholdForMergeOperationStart = 3;
    Assert.That(internalLease.Maintainer.ThresholdForMergeOperationStart, Is.EqualTo(3));
  }

  [Test]
  public void LeasePinsIdlePartitionAndClosedPartitionsCanBeReopened()
  {
    using var engine = TestEngine.Create(directory);
    Track(engine, First);
    var raw = engine.Storage.RawEvents;
    var name = raw.GetPartitions().Single();
    var lease = raw.Acquire(name);
    engine.RawEventStore.Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    Assert.That(engine.RawEventStore.Store.OpenPartitionCount, Is.EqualTo(1));
    using (var second = raw.Acquire(name))
      Assert.That(second.ZoneTree, Is.SameAs(lease.ZoneTree));
    lease.Dispose();
    lease.Dispose();
    Assert.Throws<ObjectDisposedException>(() => _ = lease.ZoneTree);
    Assert.Throws<ObjectDisposedException>(() => _ = lease.Maintainer);
    engine.RawEventStore.Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    Assert.That(engine.RawEventStore.Store.OpenPartitionCount, Is.Zero);
    Assert.That(raw.GetPartitions(), Is.EqualTo(new[] { name }));
    Assert.That(engine.RawEventStore.Store.OpenPartitionCount, Is.Zero, "discovery does not open trees");
    using var reopened = raw.Acquire(name);
    using var iterator = reopened.ZoneTree.CreateIterator();
    Assert.That(iterator.Next(), Is.True);
  }

  [Test]
  public void LeaseDefersRetentionDropUntilReleased()
  {
    using var engine = TestEngine.Create(directory, o => o.Storage.Partitioning.RawEvents = 1);
    Track(engine, First);
    var raw = engine.Storage.RawEvents;
    var name = raw.GetPartitions().Single();
    var lease = raw.Acquire(name);
    Assert.That(engine.RollOverPartitions(First.Ticks), Is.EqualTo(1));
    Track(engine, DateTime.UtcNow.AddMinutes(-5));
    var path = Path.Combine(raw.Directory, name);
    Assert.That(engine.RawEventStore.Store.DropPartitionsEndingBefore(DateTime.UtcNow.Date.Ticks), Is.Zero);
    Assert.That(raw.Acquire(name), Is.Null);
    Assert.That(Directory.Exists(path), Is.True);
    using (var iterator = lease.ZoneTree.CreateIterator())
      Assert.That(iterator.Next(), Is.True);
    lease.Dispose();
    Assert.That(Directory.Exists(path), Is.False);
    Assert.That(engine.RawEventStore.Store.DropPartitionsEndingBefore(DateTime.UtcNow.Date.Ticks), Is.EqualTo(1));
  }

  [Test]
  public void DisabledStoresAreNullAndCombinedGranularitiesAreRejected()
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.StoreRawEvents = false;
      o.Sessions.Enabled = false;
      o.Rollups.Granularities = Granularity.Day;
    });
    Assert.Multiple(() =>
    {
      Assert.That(engine.Storage.RawEvents, Is.Null);
      Assert.That(engine.Storage.Sessions, Is.Null);
      Assert.That(engine.Storage.SessionsMaintainer, Is.Null);
      Assert.That(engine.Storage.GetRollups(Granularity.Minute), Is.Null);
      Assert.That(engine.Storage.GetRollups(Granularity.Hour), Is.Null);
      Assert.That(engine.Storage.GetRollups(Granularity.Day), Is.Not.Null);
    });
    Assert.Throws<ArgumentOutOfRangeException>(() => engine.Storage.GetRollups(Granularity.Hour | Granularity.Day));
    Assert.Throws<ArgumentOutOfRangeException>(() => engine.Storage.GetRollups((Granularity)0));
  }

  [Test]
  public async Task UnknownPartitionsDoNotCreateStorageAndAsyncAccessSupportsCancellation()
  {
    using var engine = TestEngine.Create(directory);
    var raw = engine.Storage.RawEvents;
    Assert.That(raw.GetPartitions(), Is.Empty);
    Assert.That(raw.Acquire("2099-01-01"), Is.Null);
    Assert.That(raw.Acquire("../other"), Is.Null);
    Assert.That(Directory.GetDirectories(raw.Directory), Is.Empty);
    Assert.Throws<ArgumentException>(() => raw.Acquire(" "));
    Track(engine, First);
    using var lease = await raw.AcquireAsync(raw.GetPartitions().Single());
    Assert.That(lease.ZoneTree, Is.Not.Null);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Assert.CatchAsync<OperationCanceledException>(async () => await raw.AcquireAsync(lease.PartitionName, cancelled.Token));
    Assert.Throws<OperationCanceledException>(() => raw.Acquire(lease.PartitionName, cancelled.Token));
  }

  [Test]
  public void EngineDisposalInvalidatesStoredAccessorsAndLeases()
  {
    using var engine = TestEngine.Create(directory);
    Track(engine, First);
    var storage = engine.Storage;
    var raw = storage.RawEvents;
    var rollups = storage.GetRollups(Granularity.Hour);
    using var lease = raw.Acquire(raw.GetPartitions().Single());
    engine.Dispose();
    Assert.Throws<ObjectDisposedException>(() => raw.GetPartitions());
    Assert.Throws<ObjectDisposedException>(() => raw.Acquire(lease.PartitionName));
    Assert.Throws<ObjectDisposedException>(() => rollups.GetPartitions());
    Assert.Throws<ObjectDisposedException>(() => storage.GetRollups(Granularity.Hour));
    Assert.Throws<ObjectDisposedException>(() => _ = storage.Sessions);
    Assert.Throws<ObjectDisposedException>(() => _ = storage.SessionsMaintainer);
    Assert.Throws<ObjectDisposedException>(() => _ = lease.ZoneTree);
    Assert.Throws<ObjectDisposedException>(() => _ = lease.Maintainer);
  }
}
