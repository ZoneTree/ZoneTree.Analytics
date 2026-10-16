using ZoneTree.Analytics.Internal;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

sealed record RollupIndex(string Dimension, string DefinitionId, Granularity Granularity,
    PartitionedStore<Memory<byte>, RollupValue> Store)
{
  public byte[] KeyPrefix { get; } = RollupIndexKeyCodec.Namespace(Dimension, DefinitionId);
}

/// <summary>Recoverable, absolute copies of authoritative joint rollup rows.</summary>
sealed class RollupIndexStore : IDisposable
{
  const string CleanKey = "rollup-index.clean";
  readonly string root;
  readonly AnalyticsOptions options;
  readonly RollupStore rollups;
  readonly StoreFactory factory;
  readonly MetaStore meta;
  readonly ILogger logger;
  readonly List<RollupIndex> indexes = [];
  readonly List<IDisposable> resources = [];
  readonly Dictionary<Granularity, PartitionedStore<Memory<byte>, RollupValue>> stores = [];
  Dictionary<(Granularity Granularity, string DefinitionId), RollupIndex[]> copies = [];
  readonly Dictionary<Granularity, long> removed = [];
  readonly Dictionary<Granularity, IndexRecovery> recovery = [];
  int invalid;
  long revision;
  string manifest;

  public RollupIndexStore(string root, AnalyticsOptions options, RollupStore rollups, StoreFactory factory, MetaStore meta, ILogger logger)
  {
    this.root = root;
    this.options = options;
    this.rollups = rollups;
    this.factory = factory;
    this.meta = meta;
    this.logger = logger;
  }

  public IReadOnlyList<RollupIndex> Indexes => indexes;
  public bool IsHealthy => Volatile.Read(ref invalid) == 0;
  public IEnumerable<PartitionedStore<Memory<byte>, RollupValue>> AllStores => stores.Values;

  static string Registration(string dimension, string definitionId, Granularity granularity) =>
      string.Join(KeyCodec.ValueSeparator, dimension, definitionId, granularity.ToString());

  public void Initialize(RawEventStore rawEvents, RollupActivations activations, ReportingCalendar calendar, Ingestion.SessionSchema schema)
  {
    using var startup = new StartupLog(logger, "Rollup index initialization", root);
    using var check = new StartupLog(logger, "Rollup index backfill check", root);
    var format = meta.Get(StorageFormats.RollupIndexKey);
    if (format != null)
      StorageFormats.Require(format, StorageFormats.RollupIndex, "The rollup indexes of the analytics data directory");
    var configured = options.Storage.RollupIndexes.Definitions.SelectMany(definition => RollupStore.Granularities
        .Where(granularity => rollups.IsEnabled(granularity))
        .Select(granularity => Registration(definition.Dimension, definition.Definition.Id, granularity)))
        .Order(StringComparer.Ordinal).ToArray();
    manifest = string.Join('\n', configured);
    var reasons = new List<string>();
    // Indexes that were never initialized have no checkpoint, manifest or format to compare.
    if (format == null) reasons.Add("new indexes");
    else
    {
      if (meta.Get(CleanKey) != "true") reasons.Add("missing or unclean checkpoint");
      if (meta.Get("rollup-index.config") != manifest) reasons.Add("configuration changed");
    }
    if (!factory.RollupsDurable || !factory.RollupIndexesDurable) reasons.Add("write-ahead logging disabled");
    var partitions = 0;
    foreach (var granularity in RollupStore.Granularities.Where(granularity =>
        rollups.IsEnabled(granularity) && options.Storage.RollupIndexes.Definitions.Count > 0))
    {
      var store = Open(granularity);
      stores.Add(granularity, store);
      recovery.Add(granularity, new(meta, "rollup-index.dirty." + granularity + "."));
      foreach (var definition in options.Storage.RollupIndexes.Definitions)
        indexes.Add(new(definition.Dimension, definition.Definition.Id, granularity, store));
      var names = store.ListPartitions();
      partitions += names.Count;
      var expected = (meta.Get(PartitionsKey(granularity)) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
      if (IndexRecoveryScan.MissingRetainedPartitions(expected, names, factory.RollupRetentionCutoff(granularity)))
        reasons.Add($"partition manifest changed: {granularity}");
    }
    copies = indexes.GroupBy(index => (index.Granularity, index.DefinitionId)).ToDictionary(group => group.Key, group => group.ToArray());
    var rebuild = reasons.Count > 0;
    var dirtyDays = recovery.ToDictionary(pair => pair.Key, pair => pair.Value.Days);
    var dirtyCount = dirtyDays.Values.Sum(days => days.Length);
    var decision = indexes.Count == 0 ? "disabled" : rebuild ? "rebuild required" : dirtyCount > 0 ? "repair affected days" : "reuse existing indexes unless historical rollups change";
    var reason = indexes.Count == 0 ? "no configured combinations or enabled tiers" : rebuild ? string.Join(", ", reasons) : dirtyCount > 0 ? "durable write intents require reconciliation" : "clean checkpoint and matching manifests";
    check.Complete($"decision={decision}; indexes={indexes.Count}; physical stores={stores.Count}; partitions={partitions}; dirty days={dirtyCount}; reasons={reason}");
    // Disabled tiers must not retain copies. Only the three known tier directories are owned here.
    foreach (var granularity in RollupStore.Granularities.Where(granularity => !stores.ContainsKey(granularity)))
    {
      if (!Directory.Exists(TierDirectory(granularity))) continue;
      var obsolete = Open(granularity);
      foreach (var partition in obsolete.ListPartitions()) obsolete.Drop(partition);
    }
    // Historical reconstruction publishes coverage only after its durable checkpoint.
    var historicalBackfill = RollupIndexBackfill.Build(options, rollups, rawEvents, activations, calendar, schema, meta, logger);
    rebuild |= historicalBackfill;
    if ((!rebuild && dirtyCount == 0) || indexes.Count == 0)
    {
      MarkClean();
      startup.Complete($"decision={(indexes.Count == 0 ? "disabled" : "reuse existing indexes")}; indexes={indexes.Count}; physical stores={stores.Count}");
      return;
    }
    Backfill(rebuild ? null : dirtyDays, historicalBackfill);
    MarkClean();
    startup.Complete($"decision={(rebuild ? "rebuild completed" : "repair completed")}; indexes={indexes.Count}; physical stores={stores.Count}");
  }

  void Backfill(Dictionary<Granularity, long[]> dirtyDays, bool historicalBackfill)
  {
    using var build = new StartupLog(logger, "Rollup index backfill", root);
    var sources = RollupStore.Granularities.Where(rollups.IsEnabled).SelectMany(granularity =>
        rollups.GetStore(granularity).ListPartitions().Select(partition => (Granularity: granularity, Partition: partition,
            Id: ((int)granularity).ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ":" + partition))).OrderBy(source => source.Id, StringComparer.Ordinal).ToArray();
    var signature = manifest + "\n" + string.Join(',', sources.Select(source => source.Id)) + "\n" +
        (dirtyDays == null ? "all" : string.Join(';', dirtyDays.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + string.Join(',', pair.Value))));
    var checkpoint = new IndexBackfillCheckpoint(meta, "rollup-index.backfill", signature);
    if (dirtyDays == null) meta.Set(CleanKey, "false");
    if (!checkpoint.Resuming) checkpoint.Begin();
    if (!checkpoint.Copying)
    {
      foreach (var (granularity, store) in stores)
        foreach (var partition in store.ListPartitions())
        {
          if (dirtyDays == null) { store.Drop(partition); continue; }
          using var lease = store.Acquire(partition);
          if (lease == null) continue;
          IndexRecoveryScan.Visit(lease.Tree, dirtyDays[granularity], RollupIndexKeyCodec.HeadLength,
              (key, _) => lease.Tree.ForceDelete(key));
        }
      IndexRecoveryScan.Persist(AllStores);
      checkpoint.Cleared();
    }
    long count = checkpoint.Count;
    var resumed = count;
    long rows = 0;
    long matched = 0;
    var sourcePartitions = 0;
    var resumePartition = checkpoint.Partition;
    var resumeKey = checkpoint.Key;
    foreach (var source in sources)
      {
        if (string.CompareOrdinal(source.Id, resumePartition) < 0) continue;
        if (dirtyDays != null && (!dirtyDays.TryGetValue(source.Granularity, out var selectedDays) || selectedDays.Length == 0)) continue;
        using var lease = rollups.GetStore(source.Granularity).Acquire(source.Partition);
        if (lease == null) continue;
        sourcePartitions++;
        Memory<byte> lastKey = default;
        IndexRecoveryScan.Visit(lease.Tree, dirtyDays?.GetValueOrDefault(source.Granularity), KeyCodec.RollupHead, (key, value) =>
        {
          lastKey = key;
          var written = CopyCore(source.Granularity, key, value);
          rows += written;
          if (written > 0) matched++;
          if (++count % IndexBackfillCheckpoint.Interval == 0)
          {
            IndexRecoveryScan.Persist(AllStores);
            checkpoint.Save(source.Id, lastKey, count);
            build.Progress($"checkpoint saved; rollup rows scanned={count}; resumed rows={resumed}; indexed source rows={matched}; index rows written={rows}", count - resumed, "rows");
          }
        }, source.Id == resumePartition ? resumeKey : default);
        IndexRecoveryScan.Persist(AllStores);
        checkpoint.Save(source.Id, lastKey.IsEmpty && source.Id == resumePartition ? resumeKey : lastKey, count);
        build.Progress($"checkpoint saved; partition={source.Id}; rollup rows scanned={count}; resumed rows={resumed}", count - resumed, "rows");
      }
    // Release replayed snapshots and drain index WALs before reporting the build as ready.
    foreach (var store in AllStores)
    {
      store.Compact();
      store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    }
    build.Complete($"indexes={indexes.Count}; physical stores={stores.Count}; source partitions={sourcePartitions}; rollup rows scanned={count}; resumed rows={resumed}; indexed source rows={matched}; index rows written={rows}; historical backfill={historicalBackfill}", count - resumed, "rows");
  }

  static string PartitionsKey(Granularity granularity) => "rollup-index.partitions." + granularity;

  string TierDirectory(Granularity granularity) => Path.Combine(root, granularity.ToString().ToLowerInvariant());

  PartitionedStore<Memory<byte>, RollupValue> Open(Granularity granularity)
  {
    var store = new PartitionedStore<Memory<byte>, RollupValue>(TierDirectory(granularity), options.Storage.RollupIndexes.RecordsPerPartition,
        directory => factory.OpenRollupIndex(directory, granularity), logger, factory.CreateRollupIndexMaintainer, factory.WriteAheadLogDirectoryOf);
    resources.Add(store);
    return store;
  }

  public void Copy(Granularity granularity, Memory<byte> key, RollupValue value)
  {
    if (!IsHealthy || indexes.Count == 0) return;
    try { CopyCore(granularity, key, value); }
    catch (Exception exception)
    {
      Volatile.Write(ref invalid, 1);
      Interlocked.Increment(ref revision);
      // Primary rollups remain authoritative and must keep accepting deltas.
      try { logger.LogError(exception); } catch { }
    }
  }

  public void BeforeWrite(Granularity granularity, Memory<byte> key)
  {
    if (!recovery.TryGetValue(granularity, out var journal)) return;
    var definition = KeyCodec.ReadDimensionSetId(key.Span);
    if (copies.ContainsKey((granularity, definition))) journal.Track(KeyCodec.ReadRollupBucket(key.Span));
  }

  int CopyCore(Granularity granularity, Memory<byte> originalKey, RollupValue value)
  {
    // Ordinary rollup rows do not need dimension arrays/dictionaries merely to discover
    // that their combination is not indexed.
    var definitionId = KeyCodec.ReadDimensionSetId(originalKey.Span);
    if (!copies.TryGetValue((granularity, definitionId), out var destinations)) return 0;
    var key = StorageKeyCodec.DecodeRollup(originalKey.Span);
    var store = destinations[0].Store;
    using var lease = store.Acquire(store.RouteWrite(key.BucketTicks));
    if (lease == null) return 0;
    var written = 0;
    foreach (var index in destinations)
    {
      var match = KeyCodec.Stored(key.Dimensions[index.Dimension]);
      lease.Tree.Upsert(RollupIndexKeyCodec.Key(index.KeyPrefix, key.Site, key.Event, match, key.BucketTicks, originalKey.Span), value);
      written++;
    }
    return written;
  }

  public string Scan(Granularity granularity, string site, string eventName, string definitionId, long from, long to,
      IEnumerable<Filter> filters, Action<long, string, RollupValue> visit, CancellationToken cancellationToken)
  {
    if (!IsHealthy || from >= to) return null;
    foreach (var filter in DimensionIndexStore.Mandatory(filters))
    {
      if (!DimensionIndexStore.TryEquality(filter, out var dimension, out var values)) continue;
      var index = indexes.FirstOrDefault(index => index.Dimension == dimension && index.DefinitionId == definitionId && index.Granularity == granularity);
      if (index == null) continue;
      values = values.Distinct(StringComparer.Ordinal).ToArray();
      if (values.Length > 64) continue;
      var currentRevision = Interlocked.Read(ref revision);
      Check(currentRevision);
      foreach (var partition in index.Store.PartitionsInRange(from, to))
      {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = index.Store.Acquire(partition.Name);
        if (lease == null) continue;
        foreach (var value in values)
        {
          var prefix = RollupIndexKeyCodec.Prefix(index.KeyPrefix, site, eventName, value);
          using var iterator = lease.Tree.CreateIterator(new IteratorOptions { DiskSegmentPrefetchSize = 32, ContributeToTheBlockCache = true });
          iterator.Seek(KeyCodec.SeekKey(prefix, Math.Max(from, partition.From)));
          var count = 0;
          while (iterator.Next())
          {
            if (++count % RawEventStore.CancellationCheckInterval == 0) cancellationToken.ThrowIfCancellationRequested();
            var key = iterator.CurrentKey.Span;
            if (!key.StartsWith(prefix)) break;
            var bucket = KeyCodec.ReadBucket(key, prefix.Length);
            if (bucket >= Math.Min(to, partition.To)) break;
            var original = key[(prefix.Length + 8)..];
            visit(bucket, KeyCodec.ReadValue(original, KeyCodec.HeadLength(original, 3)), iterator.CurrentValue);
          }
        }
      }
      Check(currentRevision);
      return dimension;
    }
    return null;
  }

  void Check(long expectedRevision)
  {
    if (!IsHealthy || expectedRevision != Interlocked.Read(ref revision))
      throw new AnalyticsQueryException("Rollup indexes changed during the query. Retry the query.");
  }

  public int ApplyRetention(RetentionCutoffs cutoffs, RetentionClock clock)
  {
    var dropped = 0;
    foreach (var granularity in RollupStore.Granularities)
    {
      var cutoff = cutoffs.RollupCutoff(granularity);
      if (cutoff == long.MinValue) continue;
      if (stores.TryGetValue(granularity, out var store))
      {
        dropped += store.DropPartitionsEndingBefore(cutoff);
        if (removed.GetValueOrDefault(granularity, long.MinValue) == cutoff) continue;
        ExpiredKeyPredicate expired = key => clock.IsRollupExpired(granularity, RollupIndexKeyCodec.OriginalKey(key));
        store.RemoveExpired(cutoff, tree => ExpiredRecords.DeleteFirstOfExpiredHeads(tree, RollupIndexKeyCodec.HeadLength, expired));
      }
      removed[granularity] = cutoff;
    }
    return dropped;
  }

  public void MarkClean()
  {
    if (!IsHealthy) return;
    meta.Update(entries =>
    {
      entries[CleanKey] = factory.RollupsDurable && factory.RollupIndexesDurable ? "true" : "false";
      entries[StorageFormats.RollupIndexKey] = StorageFormats.Text(StorageFormats.RollupIndex);
      entries["rollup-index.config"] = manifest;
      foreach (var (granularity, store) in stores) entries[PartitionsKey(granularity)] = string.Join(',', store.ListPartitions());
      foreach (var journal in recovery.Values) journal.Clear(entries);
      new IndexBackfillCheckpoint(meta, "rollup-index.backfill", manifest).Clear(entries);
    });
  }

  public void Dispose() => Disposal.DisposeAll(resources);
}
