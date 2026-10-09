using ZoneTree.Analytics.Ingestion;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Pre-aggregated rows of every enabled granularity.
/// </summary>
/// <remarks>
/// Each granularity has its own partitioned store, partitioned as configured in
/// <see cref="PartitioningOptions"/> (by default minute rows by day, hour rows by month and
/// day rows by year), so retention can drop whole partitions.
/// Rows are merged with ZoneTree's atomic add-or-update, which keeps concurrent
/// flushes from several ingestion workers consistent.
/// </remarks>
sealed class RollupStore : IDisposable
{
  readonly PartitionedStore<Memory<byte>, RollupValue>[] stores = new PartitionedStore<Memory<byte>, RollupValue>[3];

  readonly RollupDefinition[] definitions;

  public RollupStore(
      string rootDirectory,
      Granularity granularities,
      PartitioningOptions partitioning,
      RollupDefinition[] definitions,
      StoreFactory factory,
      ILogger logger)
  {
    this.definitions = definitions;
    if ((granularities & Granularity.Minute) != 0)
      stores[0] = new(Path.Combine(rootDirectory, "minute"), new PartitionScheme(partitioning.MinuteRollups), factory.OpenRollups, logger);
    if ((granularities & Granularity.Hour) != 0)
      stores[1] = new(Path.Combine(rootDirectory, "hour"), new PartitionScheme(partitioning.HourRollups), factory.OpenRollups, logger);
    if ((granularities & Granularity.Day) != 0)
      stores[2] = new(Path.Combine(rootDirectory, "day"), new PartitionScheme(partitioning.DayRollups), factory.OpenRollups, logger);
  }

  static int IndexOf(Granularity granularity) => granularity switch
  {
    Granularity.Minute => 0,
    Granularity.Hour => 1,
    Granularity.Day => 2,
    _ => throw new ArgumentOutOfRangeException(nameof(granularity))
  };

  public PartitionedStore<Memory<byte>, RollupValue> GetStore(Granularity granularity) =>
      stores[IndexOf(granularity)];

  public bool IsEnabled(Granularity granularity) => stores[IndexOf(granularity)] != null;

  public IEnumerable<PartitionedStore<Memory<byte>, RollupValue>> AllStores => stores.Where(x => x != null);

  /// <summary>
  /// Merges buffered rows into storage.
  /// </summary>
  public void Write(Dictionary<RollupBufferKey, RollupAccumulator> rows)
  {
    if (rows.Count == 0)
      return;
    var groups = new Dictionary<(Granularity, string), List<KeyValuePair<Memory<byte>, RollupValue>>>();
    foreach (var (key, accumulator) in rows)
    {
      var store = GetStore(key.Granularity);
      var partition = store.Scheme.NameOf(key.Bucket);
      if (!groups.TryGetValue((key.Granularity, partition), out var list))
      {
        list = [];
        groups.Add((key.Granularity, partition), list);
      }
      var definition = definitions[key.Definition];
      var storageKey = KeyCodec.RollupKey(key.Site, key.Event, definition.Id, key.Bucket, key.Value);
      list.Add(new(storageKey, accumulator.ToValue()));
    }
    foreach (var ((granularity, partition), list) in groups)
    {
      using var lease = GetStore(granularity).Acquire(partition, create: true);
      if (lease == null)
        continue; // The partition is being dropped by retention.
      var tree = lease.Tree;
      foreach (var (key, delta) in list)
      {
        tree.TryAtomicAddOrUpdate(
            key,
            delta,
            (ref RollupValue existing) =>
            {
              // Never mutate the stored instance: merge creates a new value.
              existing = RollupValue.Merge(existing, delta);
              return true;
            });
      }
    }
  }

  /// <summary>
  /// Scans the rows of one series whose bucket is in [fromBucket, toBucket).
  /// </summary>
  public void Scan(
      Granularity granularity,
      string site,
      string eventName,
      string dimensionSetId,
      long fromBucket,
      long toBucket,
      Action<long, string, RollupValue> visit)
  {
    var store = GetStore(granularity);
    if (store == null || fromBucket >= toBucket)
      return;
    var prefix = KeyCodec.RollupPrefix(site, eventName, dimensionSetId);
    var seekKey = KeyCodec.SeekKey(prefix, fromBucket);
    foreach (var partition in store.Scheme.NamesInRange(fromBucket, toBucket))
    {
      using var lease = store.Acquire(partition, create: false);
      if (lease == null)
        continue;
      using var iterator = lease.Tree.CreateIterator(new IteratorOptions
      {
        DiskSegmentPrefetchSize = 32
      });
      iterator.Seek(seekKey);
      while (iterator.Next())
      {
        var key = iterator.CurrentKey.Span;
        if (key.Length < prefix.Length + 8 || !key[..prefix.Length].SequenceEqual(prefix))
          break;
        var bucket = KeyCodec.ReadBucket(key, prefix.Length);
        if (bucket >= toBucket)
          break;
        visit(bucket, KeyCodec.ReadValue(key, prefix.Length), iterator.CurrentValue);
      }
    }
  }

  /// <summary>
  /// Lists the event names of a site present in the partitions overlapping the range.
  /// Uses seek-based skip scanning: one seek per distinct event name.
  /// </summary>
  public SortedSet<string> ListEventNames(Granularity granularity, string site, long fromBucket, long toBucket)
  {
    var result = new SortedSet<string>(StringComparer.Ordinal);
    var store = GetStore(granularity);
    if (store == null)
      return result;
    var sitePrefix = KeyCodec.SitePrefix(site);
    foreach (var partition in store.Scheme.NamesInRange(fromBucket, toBucket))
    {
      using var lease = store.Acquire(partition, create: false);
      if (lease == null)
        continue;
      using var iterator = lease.Tree.CreateIterator();
      iterator.Seek(sitePrefix);
      while (iterator.Next())
      {
        var key = iterator.CurrentKey.Span;
        if (!key.StartsWith(sitePrefix))
          break;
        var eventName = KeyCodec.ReadSegment(key, sitePrefix.Length, out var next);
        result.Add(eventName);
        iterator.Seek(KeyCodec.AfterSegment(key[..(next - 1)]));
      }
    }
    return result;
  }

  public void Dispose()
  {
    foreach (var store in AllStores)
      store.Dispose();
  }
}
