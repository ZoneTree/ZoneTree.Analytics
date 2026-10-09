using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// A set of time-partitioned ZoneTrees that share one schema.
/// </summary>
/// <remarks>
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

  readonly object sync = new();

  readonly Dictionary<string, Partition> partitions = new(StringComparer.Ordinal);

  readonly Func<string, IZoneTree<TKey, TValue>> openTree;

  readonly Func<IZoneTree<TKey, TValue>, IMaintainer> createMaintainer;

  readonly ILogger logger;

  bool isDisposed;

  public PartitionedStore(
      string rootDirectory,
      PartitionScheme scheme,
      Func<string, IZoneTree<TKey, TValue>> openTree,
      ILogger logger,
      Func<IZoneTree<TKey, TValue>, IMaintainer> createMaintainer = null)
  {
    RootDirectory = rootDirectory;
    Scheme = scheme;
    this.openTree = openTree;
    this.createMaintainer = createMaintainer ?? (tree => tree.CreateMaintainer());
    this.logger = logger;
    Directory.CreateDirectory(rootDirectory);
  }

  public string RootDirectory { get; }

  public PartitionScheme Scheme { get; }

  public int OpenPartitionCount
  {
    get
    {
      lock (sync)
        return partitions.Count;
    }
  }

  string PathOf(string name) => Path.Combine(RootDirectory, name);

  /// <summary>
  /// Acquires a partition. Returns null when the partition does not exist and
  /// <paramref name="create"/> is false, or when it is being dropped.
  /// </summary>
  public PartitionLease<TKey, TValue> Acquire(string name, bool create)
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
        if (!create && !Directory.Exists(PathOf(name)))
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
  /// Lists the names of existing partitions in ascending order.
  /// </summary>
  public List<string> ListPartitions()
  {
    var names = new SortedSet<string>(StringComparer.Ordinal);
    if (Directory.Exists(RootDirectory))
    {
      foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
      {
        var name = Path.GetFileName(directory);
        if (Scheme.TryParse(name, out _))
          names.Add(name);
      }
    }
    lock (sync)
    {
      foreach (var name in partitions.Keys)
        names.Add(name);
    }
    return [.. names];
  }

  /// <summary>
  /// Drops every partition that ends at or before the cutoff.
  /// Leased partitions are dropped when their last lease is released.
  /// </summary>
  public int DropPartitionsEndingBefore(long cutoffTicks)
  {
    var count = 0;
    foreach (var name in ListPartitions())
    {
      if (!Scheme.TryParse(name, out var start))
        continue;
      if (Scheme.NextStart(start) > cutoffTicks)
        continue;
      Drop(name);
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
      while (true)
      {
        if (!partitions.TryGetValue(name, out var partition))
        {
          // Not open: delete the directory directly.
          DeleteDirectory(PathOf(name));
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
  public void CloseIdle(TimeSpan idleTimeout, string keepOpen = null)
  {
    var toClose = new List<Partition>();
    lock (sync)
    {
      var now = Environment.TickCount64;
      foreach (var partition in partitions.Values)
      {
        if (partition.Leases > 0 || partition.Closing || partition.Name == keepOpen)
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

  void CloseOrDrop(Partition partition, bool drop)
  {
    try
    {
      if (drop)
      {
        partition.Maintainer.TryCancelBackgroundThreads();
        partition.Maintainer.Dispose();
        partition.Tree.Maintenance.Drop();
        partition.Tree.Dispose();
        DeleteDirectory(PathOf(partition.Name));
      }
      else
      {
        partition.Maintainer.EvictToDisk();
        partition.Maintainer.WaitForBackgroundThreads();
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
        Monitor.PulseAll(sync);
      }
    }
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
