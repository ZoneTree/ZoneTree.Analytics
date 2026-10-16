using System.Runtime.CompilerServices;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Pre-aggregated rows of every enabled granularity.
/// </summary>
/// <remarks>
/// Each granularity has its own partitioned store, which starts a new partition after the
/// number of rows configured in <see cref="PartitioningOptions"/>.
/// A <see cref="RollupWriter"/> per partition serializes the merges of each row by lock stripe
/// and resumes failed writes, which keeps concurrent writes consistent even when an upsert throws
/// after updating memory. During ingestion, the threads of the
/// <see cref="Ingestion.RollupWriterPool"/> call <see cref="Write"/>.
/// </remarks>
sealed class RollupStore : IDisposable
{
  /// <summary>
  /// The rollup granularities, finest first.
  /// </summary>
  public static readonly Granularity[] Granularities = [Granularity.Minute, Granularity.Hour, Granularity.Day];

  readonly PartitionedStore<Memory<byte>, RollupValue>[] stores = new PartitionedStore<Memory<byte>, RollupValue>[3];

  readonly RollupDefinition[] definitions;

  readonly List<IDisposable> ownedStores = [];

  readonly ConditionalWeakTable<IZoneTree<Memory<byte>, RollupValue>, RollupWriter> writers = new();

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
      stores[0] = new(Path.Combine(rootDirectory, "minute"), partitioning.MinuteRollups, directory => factory.OpenRollups(directory, Granularity.Minute), logger, factory.CreateRollupsMaintainer, factory.WriteAheadLogDirectoryOf);
    if ((granularities & Granularity.Hour) != 0)
      stores[1] = new(Path.Combine(rootDirectory, "hour"), partitioning.HourRollups, directory => factory.OpenRollups(directory, Granularity.Hour), logger, factory.CreateRollupsMaintainer, factory.WriteAheadLogDirectoryOf);
    if ((granularities & Granularity.Day) != 0)
      stores[2] = new(Path.Combine(rootDirectory, "day"), partitioning.DayRollups, directory => factory.OpenRollups(directory, Granularity.Day), logger, factory.CreateRollupsMaintainer, factory.WriteAheadLogDirectoryOf);
    ownedStores.AddRange(PrimaryStores);
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

  public RollupIndexStore Indexes { get; set; }

  public IEnumerable<PartitionedStore<Memory<byte>, RollupValue>> PrimaryStores => stores.Where(x => x != null);

  public IEnumerable<PartitionedStore<Memory<byte>, RollupValue>> AllStores =>
      PrimaryStores.Concat(Indexes?.AllStores ?? []);

  /// <summary>
  /// Merges a flush into storage, retaining progress and leases if a write fails.
  /// </summary>
  public void Write(RollupWriteBatch batch)
  {
    if (batch.Partitions == null)
    {
      // Preparation can fail too: keep the detached accumulators until it completes.
      batch.Partitions = PrepareWrites(batch.Rows);
      batch.Rows = null;
    }
    for (; batch.NextPartition < batch.Partitions.Count; ++batch.NextPartition)
    {
      var partition = batch.Partitions[batch.NextPartition];
      partition.Lease ??= GetStore(partition.Granularity).Acquire(partition.Name);
      if (partition.Lease == null)
        continue; // The partition was dropped by retention.
      var granularity = partition.Granularity;
      var writer = writers.GetValue(partition.Lease.Tree, tree => new RollupWriter(tree,
          (key, value) => Indexes?.Copy(granularity, key, value), key => Indexes?.BeforeWrite(granularity, key)));
      for (; partition.NextRow < partition.Rows.Count; ++partition.NextRow)
        writer.Write(partition.Rows[partition.NextRow]);
      partition.Lease.Dispose();
      partition.Lease = null;
    }
  }

  List<RollupWriteBatch.PartitionWrites> PrepareWrites(Dictionary<RollupBufferKey, RollupAccumulator> rows)
  {
    var groups = new Dictionary<(Granularity, string), RollupWriteBatch.PartitionWrites>();
    foreach (var (key, accumulator) in rows)
    {
      var store = GetStore(key.Granularity);
      var partition = store.RouteWrite(key.Bucket);
      if (!groups.TryGetValue((key.Granularity, partition), out var writes))
      {
        writes = new(key.Granularity, partition);
        groups.Add((key.Granularity, partition), writes);
      }
      var definition = definitions[key.Definition];
      var storageKey = KeyCodec.RollupKey(key.Site, key.Event, definition.Id, key.Bucket, key.Value);
      writes.Rows.Add(new(storageKey, accumulator.ToValue()));
    }
    return [.. groups.Values];
  }

  /// <summary>Startup-only absolute replacements for an atomic backfill checkpoint.</summary>
  public List<JointBackfillReplacement> BackfillReplacements(Dictionary<RollupBufferKey, RollupAccumulator> rows)
  {
    var result = new List<JointBackfillReplacement>(rows.Count);
    var serializer = new RollupValueSerializer();
    foreach (var partition in PrepareWrites(rows))
    {
      using var lease = GetStore(partition.Granularity).Acquire(partition.Name);
      if (lease == null) continue;
      foreach (var row in partition.Rows)
      {
        lease.Tree.TryGet(row.Key, out var existing);
        result.Add(new()
        {
          Granularity = partition.Granularity, Partition = partition.Name, Key = row.Key.ToArray(),
          Value = serializer.Serialize(RollupValue.Merge(existing, row.Delta)).ToArray()
        });
      }
    }
    return result;
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
    foreach (var partition in store.PartitionsInRange(fromBucket, toBucket))
    {
      using var lease = store.Acquire(partition.Name);
      if (lease == null)
        continue;
      // Dashboards repeat rollup queries: cached blocks spare decompressing them again.
      using var iterator = lease.Tree.CreateIterator(new IteratorOptions
      {
        DiskSegmentPrefetchSize = 32,
        ContributeToTheBlockCache = true
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
  /// Lists event names with a positive event count in [fromBucket, toBucket).
  /// Seeks into each dimension set, and skips the remaining sets once an event is found.
  /// </summary>
  public SortedSet<string> ListEventNames(
      Granularity granularity, string site, long fromBucket, long toBucket, CancellationToken cancellationToken)
  {
    var result = new SortedSet<string>(StringComparer.Ordinal);
    var store = GetStore(granularity);
    if (store == null || fromBucket >= toBucket)
      return result;
    var sitePrefix = KeyCodec.SitePrefix(site);
    foreach (var partition in store.PartitionsInRange(fromBucket, toBucket))
    {
      using var lease = store.Acquire(partition.Name);
      if (lease == null)
        continue;
      using var iterator = lease.Tree.CreateIterator();
      iterator.Seek(sitePrefix);
      while (iterator.Next())
      {
        cancellationToken.ThrowIfCancellationRequested();
        var key = iterator.CurrentKey.Span;
        if (!key.StartsWith(sitePrefix))
          break;
        var eventName = KeyCodec.ReadSegment(key, sitePrefix.Length, out var next);
        var afterEvent = KeyCodec.AfterSegment(key[..(next - 1)]);
        if (!result.Contains(eventName))
        {
          KeyCodec.ReadSegment(key, next, out var seriesLength);
          var series = key[..seriesLength].ToArray();
          iterator.Seek(KeyCodec.SeekKey(series, fromBucket));
          var count = 0;
          while (iterator.Next())
          {
            if (++count % RawEventStore.CancellationCheckInterval == 0)
              cancellationToken.ThrowIfCancellationRequested();
            key = iterator.CurrentKey.Span;
            if (!key.StartsWith(series) || KeyCodec.ReadBucket(key, series.Length) >= toBucket)
              break;
            if (iterator.CurrentValue.Count > 0)
            {
              result.Add(eventName);
              break;
            }
          }
          if (!result.Contains(eventName))
          {
            iterator.Seek(KeyCodec.AfterSegment(series.AsSpan(0, series.Length - 1)));
            continue;
          }
        }
        iterator.Seek(afterEvent);
      }
    }
    return result;
  }

  public void Dispose() => Disposal.DisposeAll(ownedStores);
}
