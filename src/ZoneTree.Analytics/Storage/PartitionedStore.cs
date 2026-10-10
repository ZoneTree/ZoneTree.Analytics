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

  sealed class Partition
  {
    public string Name;

    public IZoneTree<TKey, TValue> Tree;

    public IMaintainer Maintainer;

    public int Leases;

    public long LastUsedTicks;

    public bool DropRequested;

    public bool Closing;
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

  volatile PartitionMap map;

  bool isDisposed;

  public PartitionedStore(
      string rootDirectory,
      long recordsPerPartition,
      Func<string, IZoneTree<TKey, TValue>> openTree,
      ILogger logger,
      Func<IZoneTree<TKey, TValue>, IMaintainer> createMaintainer,
      Func<string, string> writeAheadLogDirectoryOf)
  {
    RootDirectory = rootDirectory;
    this.recordsPerPartition = recordsPerPartition;
    this.openTree = openTree;
    this.createMaintainer = createMaintainer;
    this.writeAheadLogDirectoryOf = writeAheadLogDirectoryOf;
    this.logger = logger;
    Directory.CreateDirectory(rootDirectory);
    map = LoadMap(rootDirectory);
  }

  public string RootDirectory { get; }

  public int OpenPartitionCount
  {
    get
    {
      lock (sync)
        return partitions.Count;
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
          if (partition.Closing)
          {
            Monitor.Wait(sync);
            continue;
          }
          if (partition.DropRequested)
            return null;
          ++partition.Leases;
          partition.LastUsedTicks = Environment.TickCount64;
          return new PartitionLease<TKey, TValue>(this, name, partition.Tree);
        }
        if (Array.IndexOf(map.Names, name) < 0)
          return null;
        var tree = openTree(PathOf(name));
        IMaintainer maintainer;
        try
        {
          maintainer = createMaintainer(tree);
        }
        catch
        {
          tree.Dispose();
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
        return new PartitionLease<TKey, TValue>(this, name, tree);
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
      if (partition.Leases == 0 && partition.DropRequested && !partition.Closing)
      {
        partition.Closing = true;
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
          latest.Closing ||
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
  /// </summary>
  public int DropPartitionsEndingBefore(long cutoffTicks)
  {
    var current = map;
    var count = 0;
    for (var i = 0; i < current.Count - 1 && current.To(i) <= cutoffTicks; ++i)
    {
      Drop(current.Names[i]);
      ++count;
    }
    return count;
  }

  /// <summary>
  /// Drops a partition and deletes its files.
  /// </summary>
  public void Drop(string name)
  {
    Partition dropNow = null;
    lock (sync)
    {
      map = map.Remove(name);
      closedRecordCounts.Remove(name);
      while (true)
      {
        if (!partitions.TryGetValue(name, out var partition))
        {
          // Not open: delete the files directly.
          DeletePartitionFiles(name);
          return;
        }
        if (partition.Closing)
        {
          Monitor.Wait(sync);
          continue;
        }
        partition.DropRequested = true;
        if (partition.Leases == 0)
        {
          partition.Closing = true;
          dropNow = partition;
        }
        break;
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
      var now = Environment.TickCount64;
      var latest = map.Count == 0 ? null : map.Names[^1];
      foreach (var partition in partitions.Values)
      {
        if (partition.Leases > 0 || partition.Closing || (keepLatestOpen && partition.Name == latest))
          continue;
        if (now - partition.LastUsedTicks < idleTimeout.TotalMilliseconds)
          continue;
        partition.Closing = true;
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
          partitions.TryGetValue(name, out var partition) ? partition.Tree.Maintenance.TotalRecordCount :
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
        if (partition.Closing || partition.DropRequested)
          continue;
        ++partition.Leases;
        open.Add((partition.Maintainer, new PartitionLease<TKey, TValue>(this, partition.Name, partition.Tree)));
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

  void CloseOrDrop(Partition partition, bool drop)
  {
    long? recordCount = null;
    try
    {
      if (drop)
      {
        partition.Maintainer.TryCancelBackgroundThreads();
        partition.Maintainer.Dispose();
        partition.Tree.Maintenance.Drop();
        partition.Tree.Dispose();
        DeletePartitionFiles(partition.Name);
      }
      else
      {
        partition.Maintainer.EvictToDisk();
        partition.Maintainer.WaitForBackgroundThreads();
        recordCount = partition.Tree.Maintenance.TotalRecordCount;
        partition.Maintainer.Dispose();
        partition.Tree.Dispose();
      }
    }
    catch (Exception e)
    {
      logger.LogError(e);
    }
    finally
    {
      lock (sync)
      {
        partitions.Remove(partition.Name);
        if (recordCount.HasValue && Array.IndexOf(map.Names, partition.Name) >= 0)
          closedRecordCounts[partition.Name] = recordCount.Value;
        Monitor.PulseAll(sync);
      }
    }
  }

  /// <summary>
  /// Deletes the data and, when it is kept elsewhere, the write-ahead log of a partition.
  /// </summary>
  void DeletePartitionFiles(string name)
  {
    DeleteDirectory(PathOf(name));
    if (writeAheadLogDirectoryOf(PathOf(name)) is { } walDirectory)
      DeleteDirectory(walDirectory);
  }

  void DeleteDirectory(string path)
  {
    try
    {
      if (Directory.Exists(path))
        Directory.Delete(path, recursive: true);
    }
    catch (Exception e)
    {
      logger.LogError(e);
    }
  }

  public void Dispose()
  {
    List<Partition> all;
    lock (sync)
    {
      if (isDisposed)
        return;
      isDisposed = true;
      // Let in-flight close and drop operations finish; they own their trees.
      while (partitions.Values.Any(x => x.Closing))
        Monitor.Wait(sync);
      all = [.. partitions.Values];
      partitions.Clear();
      Monitor.PulseAll(sync);
    }
    foreach (var partition in all)
    {
      try
      {
        partition.Maintainer.Dispose();
        partition.Tree.Dispose();
      }
      catch (Exception e)
      {
        logger.LogError(e);
      }
    }
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

  public PartitionLease(PartitionedStore<TKey, TValue> store, string name, IZoneTree<TKey, TValue> tree)
  {
    this.store = store;
    Name = name;
    Tree = tree;
  }

  public string Name { get; }

  public IZoneTree<TKey, TValue> Tree { get; }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref released, 1) == 0)
      store.Release(Name);
  }
}
