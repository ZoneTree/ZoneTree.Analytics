using System.Globalization;
using ZoneTree.Analytics.Internal;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// A sequence of ZoneTrees that share one schema, each holding a time range of the data.
/// </summary>
/// <remarks>
/// <para>
/// Partitions are sized by record count, not by calendar: records go to the latest partition
/// until it holds <c>recordsPerPartition</c> records, then <see cref="RollOver"/> starts a new
/// partition at a later day boundary. Partition i holds the records from its start up to the
/// start of partition i + 1; the first partition also holds older records and the last one
/// newer records. Each partition directory is named after its start day: "2026-10-11".
/// </para>
/// <para>
/// Partitions are opened on demand and closed after an idle timeout. Callers use a
/// partition through a <see cref="PartitionLease{TKey, TValue}"/>; leased partitions
/// are never closed or dropped underneath them.
/// </para>
/// <para>
/// Retention drops whole partitions with <c>Maintenance.Drop()</c>, which removes the
/// data without writing tombstones or waiting for compaction.
/// </para>
/// <para>
/// A partition directory is never opened by two ZoneTree instances: closing and dropping
/// keep the partition registered until the underlying tree is fully released, and
/// concurrent acquirers wait for it.
/// </para>
/// </remarks>
sealed class PartitionedStore<TKey, TValue> : IDisposable
{
  const string NameFormat = "yyyy-MM-dd";

  enum PartitionState { Open, Closing, CleanupFailed, Closed }

  sealed class Partition
  {
    public string Name;

    public IZoneTree<TKey, TValue> Tree;

    public IMaintainer Maintainer;

    public int Leases;

    public long LastUsedTicks;

    public bool DropRequested;

    public PartitionState State;

    public bool Disposing;

    public bool TreeDropped;

    public bool FilesDeleted;

    public long? RecordCount;

    public Exception CleanupFailure;
  }

  /// <summary>
  /// The partitions in time order. Replaced, never modified, so readers route without a lock.
  /// </summary>
  sealed class PartitionMap(long[] starts, string[] names)
  {
    public static PartitionMap Empty { get; } = new([], []);

    public long[] Starts { get; } = starts;

    public string[] Names { get; } = names;

    public int Count => Starts.Length;

    public long From(int index) => index == 0 ? long.MinValue : Starts[index];

    public long To(int index) => index == Starts.Length - 1 ? long.MaxValue : Starts[index + 1];

    /// <summary>
    /// The partition that holds a time.
    /// </summary>
    public int IndexOf(long ticks)
    {
      var index = Array.BinarySearch(Starts, ticks);
      return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    public PartitionMap Add(long start, string name) => new([.. Starts, start], [.. Names, name]);

    public PartitionMap Remove(string name)
    {
      var index = Array.IndexOf(Names, name);
      if (index < 0)
        return this;
      return new([.. Starts[..index], .. Starts[(index + 1)..]], [.. Names[..index], .. Names[(index + 1)..]]);
    }
  }

  readonly object sync = new();

  readonly Dictionary<string, Partition> partitions = new(StringComparer.Ordinal);

  /// <summary>
  /// The record counts of partitions closed during this run, reported while they stay closed.
  /// </summary>
  readonly Dictionary<string, long> closedRecordCounts = new(StringComparer.Ordinal);

  readonly long recordsPerPartition;

  readonly Func<string, IZoneTree<TKey, TValue>> openTree;

  readonly Func<IZoneTree<TKey, TValue>, IMaintainer> createMaintainer;

  readonly Func<string, string> writeAheadLogDirectoryOf;

  readonly ILogger logger;

  readonly Action<string> deleteDirectory;

  volatile PartitionMap map;

  bool isDisposed;

  int completedDrops;

  public PartitionedStore(
      string rootDirectory,
      long recordsPerPartition,
      Func<string, IZoneTree<TKey, TValue>> openTree,
      ILogger logger,
      Func<IZoneTree<TKey, TValue>, IMaintainer> createMaintainer,
      Func<string, string> writeAheadLogDirectoryOf,
      Action<string> deleteDirectory = null)
  {
    RootDirectory = rootDirectory;
    this.recordsPerPartition = recordsPerPartition;
    this.openTree = openTree;
    this.createMaintainer = createMaintainer;
    this.writeAheadLogDirectoryOf = writeAheadLogDirectoryOf;
    this.logger = logger;
    this.deleteDirectory = deleteDirectory ?? DeleteDirectory;
    Directory.CreateDirectory(rootDirectory);
    map = LoadMap(rootDirectory);
  }

  public string RootDirectory { get; }

  public int OpenPartitionCount
  {
    get
    {
      lock (sync)
        return partitions.Values.Count(x => x.Tree != null);
    }
  }

  string PathOf(string name) => Path.Combine(RootDirectory, name);

  static string NameOf(long start) => new DateTime(start).ToString(NameFormat, CultureInfo.InvariantCulture);

  static PartitionMap LoadMap(string rootDirectory)
  {
    var starts = new SortedDictionary<long, string>();
    foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
    {
      var name = Path.GetFileName(directory);
      if (DateTime.TryParseExact(name, NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        starts[start.Ticks] = name;
    }
    return new([.. starts.Keys], [.. starts.Values]);
  }

  /// <summary>
  /// The name of the partition that holds a time. The first record of a store creates its first
  /// partition, which starts at the day of that record.
  /// </summary>
  public string Route(long ticks)
  {
    var current = map;
    if (current.Count > 0)
      return current.Names[current.IndexOf(ticks)];
    lock (sync)
    {
      ObjectDisposedException.ThrowIf(isDisposed, this);
      current = map;
      if (current.Count > 0)
        return current.Names[current.IndexOf(ticks)];
      var start = ReportingCalendar.FloorTicks(ticks, TimeSpan.TicksPerDay);
      var name = NameOf(start);
      if (partitions.ContainsKey(name))
        throw new IOException($"The partition '{name}' is still being deleted; retry after cleanup succeeds.");
      Directory.CreateDirectory(PathOf(name));
      map = current.Add(start, name);
      return name;
    }
  }

  /// <summary>
  /// The partitions that hold records in [fromTicks, toTicks), in time order, with the time
  /// range each one holds.
  /// </summary>
  public List<PartitionRange> PartitionsInRange(long fromTicks, long toTicks)
  {
    var current = map;
    var result = new List<PartitionRange>();
    for (var i = 0; i < current.Count; ++i)
    {
      var from = current.From(i);
      var to = current.To(i);
      if (from < toTicks && to > fromTicks)
        result.Add(new PartitionRange(current.Names[i], from, to));
    }
    return result;
  }

  /// <summary>
  /// Lists the names of the partitions in time order.
  /// </summary>
  public List<string> ListPartitions() => [.. map.Names];

  /// <summary>
  /// Acquires a partition. Returns null when the partition no longer exists: it was dropped by
  /// retention, or is being dropped.
  /// </summary>
  public PartitionLease<TKey, TValue> Acquire(string name)
  {
    lock (sync)
    {
      while (true)
      {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (partitions.TryGetValue(name, out var partition))
        {
          if (partition.DropRequested)
            return null;
          if (partition.State == PartitionState.Closing)
          {
            Monitor.Wait(sync);
            continue;
          }
          if (partition.State == PartitionState.CleanupFailed)
            throw new IOException($"The partition '{name}' could not be closed; cleanup must succeed before it can reopen.",
                partition.CleanupFailure);
          ++partition.Leases;
          partition.LastUsedTicks = Environment.TickCount64;
          return new PartitionLease<TKey, TValue>(this, name, partition.Tree, partition.Maintainer);
        }
        if (Array.IndexOf(map.Names, name) < 0)
          return null;
        var tree = openTree(PathOf(name));
        IMaintainer maintainer;
        try
        {
          maintainer = createMaintainer(tree);
        }
        catch (Exception openFailure)
        {
          try
          {
            tree.Dispose();
          }
          catch (Exception cleanupFailure)
          {
            partitions.Add(name, new Partition
            {
              Name = name, Tree = tree, Disposing = true, State = PartitionState.CleanupFailed,
              CleanupFailure = cleanupFailure
            });
            throw new AggregateException("Creating the partition maintainer and releasing its tree both failed.",
                openFailure, cleanupFailure);
          }
          throw;
        }
        partition = new Partition
        {
          Name = name,
          Tree = tree,
          Maintainer = maintainer,
          Leases = 1,
          LastUsedTicks = Environment.TickCount64
        };
        partitions.Add(name, partition);
        closedRecordCounts.Remove(name);
        return new PartitionLease<TKey, TValue>(this, name, tree, maintainer);
      }
    }
  }

  internal void Release(string name)
  {
    Partition dropNow = null;
    lock (sync)
    {
      if (!partitions.TryGetValue(name, out var partition))
        return;
      --partition.Leases;
      partition.LastUsedTicks = Environment.TickCount64;
      if (!isDisposed && partition.Leases == 0 && partition.DropRequested && partition.State != PartitionState.Closing)
      {
        partition.State = PartitionState.Closing;
        dropNow = partition;
      }
    }
    if (dropNow != null)
      CloseOrDrop(dropNow, drop: true);
  }

  /// <summary>
  /// Starts a new partition when the latest one holds the configured number of records. The new
  /// partition starts at the day boundary at or after <paramref name="earliestStart"/>, which
  /// must be later than any record written so far: records written before the new partition
  /// existed then still belong to the range of the previous one. Only an open latest partition
  /// is counted; a closed one receives no records. Returns true when a partition was started.
  /// </summary>
  public bool RollOver(long earliestStart)
  {
    lock (sync)
    {
      if (isDisposed)
        return false;
      var current = map;
      if (current.Count == 0 ||
          !partitions.TryGetValue(current.Names[^1], out var latest) ||
          latest.State != PartitionState.Open ||
          latest.DropRequested ||
          latest.Tree.Maintenance.TotalRecordCount < recordsPerPartition)
        return false;
      var start = Math.Max(
          ReportingCalendar.FloorTicks(earliestStart + TimeSpan.TicksPerDay - 1, TimeSpan.TicksPerDay),
          current.Starts[^1] + TimeSpan.TicksPerDay);
      var name = NameOf(start);
      Directory.CreateDirectory(PathOf(name));
      map = current.Add(start, name);
      return true;
    }
  }

  /// <summary>
  /// Drops every partition that ends at or before the cutoff. The latest partition never ends.
  /// Leased partitions are dropped when their last lease is released.
  /// Returns completed deletions since the previous call, including deferred drops. Failed
  /// cleanup is retried even though its partition has already been removed from routing.
  /// </summary>
  public int DropPartitionsEndingBefore(long cutoffTicks)
  {
    List<string> pending;
    lock (sync)
      pending = [.. partitions.Values.Where(x => x.DropRequested).Select(x => x.Name)];
    foreach (var name in pending)
      Drop(name);
    var current = map;
    for (var i = 0; i < current.Count - 1 && current.To(i) <= cutoffTicks; ++i)
    {
      Drop(current.Names[i]);
    }
    return Interlocked.Exchange(ref completedDrops, 0);
  }

  /// <summary>
  /// Drops a partition and deletes its files.
  /// </summary>
  public void Drop(string name)
  {
    Partition dropNow = null;
    lock (sync)
    {
      ObjectDisposedException.ThrowIf(isDisposed, this);
      if (Array.IndexOf(map.Names, name) < 0 && !partitions.ContainsKey(name))
        return;
      map = map.Remove(name);
      closedRecordCounts.Remove(name);
      if (!partitions.TryGetValue(name, out var partition))
      {
        // Register even an unopened partition: deletion failures need an owner and a retry.
        partition = new Partition { Name = name, DropRequested = true, Disposing = true };
        partitions.Add(name, partition);
      }
      partition.DropRequested = true;
      while (partition.State == PartitionState.Closing)
      {
        Monitor.Wait(sync);
        ObjectDisposedException.ThrowIf(isDisposed, this);
      }
      // A concurrent operation may have finished this drop. Never delete a new generation
      // routed to the same name after that operation released the old directory.
      if (!partitions.TryGetValue(name, out var current) || !ReferenceEquals(current, partition))
        return;
      if (partition.Leases == 0)
      {
        partition.State = PartitionState.Closing;
        dropNow = partition;
      }
    }
    if (dropNow != null)
      CloseOrDrop(dropNow, drop: true);
  }

  /// <summary>
  /// Closes partitions that have not been used for the given time.
  /// Their in-memory data is merged to disk first, so they reopen quickly.
  /// </summary>
  public void CloseIdle(TimeSpan idleTimeout, bool keepLatestOpen)
  {
    var toClose = new List<Partition>();
    lock (sync)
    {
      if (isDisposed)
        return;
      var now = Environment.TickCount64;
      var latest = map.Count == 0 ? null : map.Names[^1];
      foreach (var partition in partitions.Values)
      {
        if (partition.Leases > 0 || partition.State == PartitionState.Closing)
          continue;
        if (!partition.Disposing && !partition.DropRequested &&
            ((keepLatestOpen && partition.Name == latest) || now - partition.LastUsedTicks < idleTimeout.TotalMilliseconds))
          continue;
        partition.State = PartitionState.Closing;
        toClose.Add(partition);
      }
    }
    foreach (var partition in toClose)
      CloseOrDrop(partition, drop: partition.DropRequested);
  }

  /// <summary>
  /// The partitions in time order with their approximate record counts: deleted records that
  /// merges have not removed yet are included. The count of a partition that was not opened
  /// during this run is null.
  /// </summary>
  public List<(string Name, long? ApproximateRecordCount)> Describe()
  {
    lock (sync)
    {
      return [.. map.Names.Select(name => (name,
          partitions.TryGetValue(name, out var partition) ?
              partition.State == PartitionState.Open ? partition.Tree.Maintenance.TotalRecordCount : partition.RecordCount :
          closedRecordCounts.TryGetValue(name, out var count) ? count : (long?)null))];
    }
  }

  /// <summary>
  /// Removes expired records from disk in every partition that holds records older than the
  /// cutoff (the partitions that end before it are dropped instead). <paramref name="deleteMarkers"/>
  /// deletes the records that make the next merge read the expired ones; a partition that needed
  /// any is merged right away. Returns the number of records deleted.
  /// </summary>
  public int RemoveExpired(long cutoff, Func<IZoneTree<TKey, TValue>, int> deleteMarkers)
  {
    var current = map;
    var deleted = 0;
    for (var i = 0; i < current.Count && current.From(i) < cutoff; ++i)
    {
      var name = current.Names[i];
      using var lease = Acquire(name);
      if (lease == null)
        continue;
      var count = deleteMarkers(lease.Tree);
      if (count == 0)
        continue;
      IMaintainer maintainer;
      lock (sync)
        maintainer = partitions[name].Maintainer;
      maintainer.EvictToDisk();
      maintainer.WaitForBackgroundThreads();
      deleted += count;
    }
    return deleted;
  }

  /// <summary>
  /// Moves the in-memory data of every open partition to disk and waits until the merges
  /// complete. Partitions merge concurrently.
  /// </summary>
  public void Compact()
  {
    var open = new List<(IMaintainer Maintainer, PartitionLease<TKey, TValue> Lease)>();
    lock (sync)
    {
      ObjectDisposedException.ThrowIf(isDisposed, this);
      foreach (var partition in partitions.Values)
      {
        if (partition.State != PartitionState.Open || partition.DropRequested)
          continue;
        ++partition.Leases;
        open.Add((partition.Maintainer, new PartitionLease<TKey, TValue>(this, partition.Name, partition.Tree, partition.Maintainer)));
      }
    }
    try
    {
      foreach (var (maintainer, _) in open)
        maintainer.EvictToDisk();
      foreach (var (maintainer, _) in open)
        maintainer.WaitForBackgroundThreads();
    }
    finally
    {
      foreach (var (_, lease) in open)
        lease.Dispose();
    }
  }

  Exception CloseOrDrop(Partition partition, bool drop, bool disposingStore = false)
  {
    Exception failure = null;
    try
    {
      if (!partition.Disposing && !drop && !disposingStore)
      {
        partition.Maintainer.EvictToDisk();
        partition.Maintainer.WaitForBackgroundThreads();
        partition.RecordCount = partition.Tree.Maintenance.TotalRecordCount;
      }
      // From here a resource may be partially disposed; it must never be leased again.
      partition.Disposing = true;
      if (partition.Maintainer != null)
      {
        if (drop)
          partition.Maintainer.TryCancelBackgroundThreads();
        partition.Maintainer.Dispose();
        partition.Maintainer = null;
      }
      if (partition.Tree != null)
      {
        if (drop && !partition.TreeDropped)
        {
          partition.Tree.Maintenance.Drop();
          partition.TreeDropped = true;
        }
        partition.Tree.Dispose();
        partition.Tree = null;
      }
      lock (sync)
        drop |= partition.DropRequested;
      if (drop)
      {
        DeletePartitionFiles(partition.Name);
        partition.FilesDeleted = true;
      }
    }
    catch (Exception e)
    {
      failure = e;
      logger.LogError(e);
    }
    finally
    {
      lock (sync)
      {
        if (failure == null && (!partition.DropRequested || partition.FilesDeleted))
        {
          partition.State = PartitionState.Closed;
          partitions.Remove(partition.Name);
          if (partition.RecordCount.HasValue && Array.IndexOf(map.Names, partition.Name) >= 0)
            closedRecordCounts[partition.Name] = partition.RecordCount.Value;
          if (partition.FilesDeleted)
            Interlocked.Increment(ref completedDrops);
        }
        else
        {
          partition.State = failure != null && partition.Disposing ? PartitionState.CleanupFailed : PartitionState.Open;
          partition.CleanupFailure = failure;
        }
        Monitor.PulseAll(sync);
      }
    }
    return failure;
  }

  /// <summary>
  /// Deletes the data and, when it is kept elsewhere, the write-ahead log of a partition.
  /// </summary>
  void DeletePartitionFiles(string name)
  {
    var failures = new List<Exception>();
    void Delete(string path)
    {
      try
      {
        deleteDirectory(path);
      }
      catch (Exception exception)
      {
        failures.Add(exception);
      }
    }
    Delete(PathOf(name));
    if (writeAheadLogDirectoryOf(PathOf(name)) is { } walDirectory)
      Delete(walDirectory);
    Disposal.ThrowFailures(failures);
  }

  static void DeleteDirectory(string path)
  {
    if (Directory.Exists(path))
      Directory.Delete(path, recursive: true);
  }

  public void Dispose()
  {
    List<Partition> all;
    lock (sync)
    {
      isDisposed = true;
      // Let in-flight close and drop operations finish; they own their trees.
      while (partitions.Values.Any(x => x.State == PartitionState.Closing))
        Monitor.Wait(sync);
      all = [.. partitions.Values];
      foreach (var partition in all)
        partition.State = PartitionState.Closing;
      Monitor.PulseAll(sync);
    }
    var failures = new List<Exception>();
    foreach (var partition in all)
    {
      if (CloseOrDrop(partition, partition.DropRequested, disposingStore: true) is { } failure)
        failures.Add(failure);
    }
    Disposal.ThrowFailures(failures);
  }
}

/// <summary>
/// A partition and the time range of the records it holds: [From, To).
/// </summary>
readonly record struct PartitionRange(string Name, long From, long To);

/// <summary>
/// A lease on an open partition. Dispose it to release the partition.
/// </summary>
sealed class PartitionLease<TKey, TValue> : IDisposable
{
  readonly PartitionedStore<TKey, TValue> store;

  int released;

  public PartitionLease(PartitionedStore<TKey, TValue> store, string name, IZoneTree<TKey, TValue> tree, IMaintainer maintainer)
  {
    this.store = store;
    Name = name;
    Tree = tree;
    Maintainer = maintainer;
  }

  public string Name { get; }

  public IZoneTree<TKey, TValue> Tree { get; }

  public IMaintainer Maintainer { get; }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref released, 1) == 0)
      store.Release(Name);
  }
}
