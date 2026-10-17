using ZoneTree.Analytics.Internal;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

sealed record RollupIndex(string Dimension, string DefinitionId, Granularity Granularity,
    PartitionedStore<Memory<byte>, RollupValue> Store)
{
  public byte[] KeyPrefix { get; } = RollupIndexKeyCodec.Namespace(Dimension, DefinitionId);
}

/// <summary>Recoverable, absolute copies of authoritative joint rollup rows.</summary>
/// <remarks>
/// Builds and repairs run on the background maintenance thread while flushes write their own
/// copies. Queries read the primary rollups until the indexes are ready. A build also folds in
/// the history that a joint rollup reconstruction rebuilt, then publishes it.
/// </remarks>
sealed class RollupIndexStore : BackgroundIndex
{
  const string KeyPrefix = "rollup-index";

  /// <summary>The prefix of the build checkpoint, which a new history reconstruction clears.</summary>
  public const string CheckpointPrefix = KeyPrefix + ".backfill";

  const string FoldingReason = "rebuilt history to fold in";
  const int BackgroundBatch = 4096;
  readonly AnalyticsOptions options;
  readonly RollupStore rollups;
  readonly StoreFactory factory;
  readonly List<RollupIndex> indexes = [];
  readonly Dictionary<Granularity, PartitionedStore<Memory<byte>, RollupValue>> stores = [];
  Dictionary<(Granularity Granularity, string DefinitionId), RollupIndex[]> copies = [];
  readonly Dictionary<Granularity, long> removed = [];
  readonly Dictionary<Granularity, IndexRecovery> recovery = [];
  RollupActivations activations;

  public RollupIndexStore(string root, AnalyticsOptions options, RollupStore rollups, StoreFactory factory, MetaStore meta, ILogger logger)
      : base(meta, logger, root, "Rollup index", KeyPrefix, "rollup-index.config",
          StorageFormats.RollupIndexKey, StorageFormats.RollupIndex, "The rollup indexes of the analytics data directory")
  {
    this.options = options;
    this.rollups = rollups;
    this.factory = factory;
  }

  public IReadOnlyList<RollupIndex> Indexes => indexes;
  public IEnumerable<PartitionedStore<Memory<byte>, RollupValue>> AllStores => stores.Values;

  static string Registration(string dimension, string definitionId, Granularity granularity) =>
      string.Join(KeyCodec.ValueSeparator, dimension, definitionId, granularity.ToString());

  /// <summary>Opens the indexes and decides their work.</summary>
  public void Initialize(RollupActivations activations)
  {
    this.activations = activations;
    var configured = options.Storage.RollupIndexes.Definitions.SelectMany(definition => RollupStore.Granularities
        .Where(granularity => rollups.IsEnabled(granularity))
        .Select(granularity => Registration(definition.Dimension, definition.Definition.Id, granularity)))
        .Order(StringComparer.Ordinal);
    // Rows of a completed history reconstruction wait for a build to fold them in.
    Open(string.Join('\n', configured), JointRollupReconstruction.HasFolding(Meta) ? [FoldingReason] : []);
  }

  protected override int IndexCount => indexes.Count;

  protected override int StoreCount => stores.Count;

  protected override string DisabledReason => "no configured combinations or enabled tiers";

  protected override bool Durable => factory.RollupsDurable && factory.RollupIndexesDurable;

  protected override IEnumerable<IndexRecovery> Journals => recovery.Values;

  protected override bool NothingToCopy =>
      rollups.PrimaryStores.All(store => store.ListPartitions().Count == 0) && !JointRollupReconstruction.IsPending(Meta);

  protected override bool Resumes(string reason) => reason is UncleanReason or FoldingReason;

  protected override int OpenStores(List<string> reasons)
  {
    var partitions = 0;
    foreach (var granularity in RollupStore.Granularities.Where(granularity =>
        rollups.IsEnabled(granularity) && options.Storage.RollupIndexes.Definitions.Count > 0))
    {
      var store = Open(granularity);
      stores.Add(granularity, store);
      recovery.Add(granularity, new(Meta, "rollup-index.dirty." + granularity + "."));
      foreach (var definition in options.Storage.RollupIndexes.Definitions)
        indexes.Add(new(definition.Dimension, definition.Definition.Id, granularity, store));
      var names = store.ListPartitions();
      partitions += names.Count;
      var expected = (Meta.Get(PartitionsKey(granularity)) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
      if (IndexRecoveryScan.MissingRetainedPartitions(expected, names, factory.RollupRetentionCutoff(granularity)))
        reasons.Add($"partition manifest changed: {granularity}");
    }
    copies = indexes.GroupBy(index => (index.Granularity, index.DefinitionId)).ToDictionary(group => group.Key, group => group.ToArray());
    // Disabled tiers must not retain copies. Only the three known tier directories are owned here.
    foreach (var granularity in RollupStore.Granularities.Where(granularity => !stores.ContainsKey(granularity)))
    {
      if (!Directory.Exists(TierDirectory(granularity))) continue;
      var obsolete = Open(granularity);
      foreach (var partition in obsolete.ListPartitions()) obsolete.Drop(partition);
    }
    return partitions;
  }

  protected override void DropCopies()
  {
    foreach (var store in stores.Values)
      foreach (var partition in store.ListPartitions()) store.Drop(partition);
  }

  protected override void SavePartitions(Dictionary<string, string> entries)
  {
    foreach (var (granularity, store) in stores) entries[PartitionsKey(granularity)] = string.Join(',', store.ListPartitions());
  }

  protected override AnalyticsStorageException BuildFailed(Exception cause) => new(
      "The rollup indexes could not be built: a copy failed. Queries read the primary rollups until the engine opens again, which resumes the build.", cause);

  /// <summary>Publishes the history the build folded in.</summary>
  protected override void Completing() =>
      JointRollupReconstruction.PublishFolded(Meta, activations, options.Rollups.Definitions);

  /// <summary>
  /// Runs the build or repair that <see cref="Initialize"/> decided, on the background
  /// maintenance thread after any history reconstruction, then makes the indexes available to
  /// queries and publishes the rebuilt history. Every attempt resumes from the checkpoint, so a
  /// failed attempt can simply be repeated.
  /// </summary>
  /// <remarks>
  /// Each row goes through the writer of its partition as a fold, under the row lock of the
  /// flushes: the fold merges the row's rebuilt part in, and the row's current value is copied.
  /// Flushes copy the rows they write themselves, so a copy is never older than its row.
  /// </remarks>
  public override void Build(CancellationToken cancellationToken)
  {
    if (IsReady) return;
    // Indexes that failed wait for the next open, which resumes them from their last checkpoint.
    RequireHealthy();
    using var build = new StageLog(Logger, "Rollup index backfill", Root);
    var checkpoint = Checkpoint();
    var historical = JointRollupReconstruction.HasFolding(Meta);
    // Rebuilt rows wait for this fold even when their definition is no longer indexed.
    var folding = JointRollupReconstruction.FoldingDefinitions(Meta, options.Rollups.Definitions);
    long count = Rebuilding ? checkpoint.Count : 0;
    var resumed = count;
    long rows = 0;
    long matched = 0;
    var sourcePartitions = 0;
    var sources = RollupStore.Granularities.Where(rollups.IsEnabled).SelectMany(granularity =>
        rollups.GetStore(granularity).ListPartitions().Select(partition => (Granularity: granularity, Partition: partition,
            Id: ((int)granularity).ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ":" + partition))).OrderBy(source => source.Id, StringComparer.Ordinal).ToArray();
    // Folds and copies the rows of a partition from a key on.
    void FoldPartition((Granularity Granularity, string Partition, string Id) source, long[] days, Memory<byte> after, bool save)
    {
      cancellationToken.ThrowIfCancellationRequested();
      using var lease = rollups.GetStore(source.Granularity).Acquire(source.Partition);
      if (lease == null) return;
      sourcePartitions++;
      var writer = rollups.WriterOf(source.Granularity, lease.Tree);
      var lastKey = after;
      IndexRecoveryScan.Visit(lease.Tree, days, KeyCodec.RollupHead, (key, _) =>
      {
        lastKey = key;
        if (++count % BackgroundBatch == 0) cancellationToken.ThrowIfCancellationRequested();
        var definition = KeyCodec.ReadDimensionSetId(key.Span);
        if (copies.TryGetValue((source.Granularity, definition), out var destinations))
        {
          writer.Write(new RollupWrite(key.ToArray(), null, RollupWriteKind.Fold));
          rows += destinations.Length;
          matched++;
        }
        else if (folding.Contains(definition))
          writer.Write(new RollupWrite(key.ToArray(), null, RollupWriteKind.Fold));
        if (save && count % IndexBackfillCheckpoint.Interval == 0)
        {
          RequireHealthy();
          checkpoint.Save(source.Id, key, count);
          build.Progress($"checkpoint saved; rollup rows scanned={count}; resumed rows={resumed}; indexed source rows={matched}; index rows written={rows}", count - resumed, "rows");
        }
      }, after);
      if (!save) return;
      RequireHealthy();
      checkpoint.Save(source.Id, lastKey, count);
      build.Progress($"checkpoint saved; partition={source.Id}; rollup rows scanned={count}; resumed rows={resumed}", count - resumed, "rows");
    }
    if (Rebuilding)
    {
      var resumePartition = checkpoint.Partition;
      var resumeKey = checkpoint.Key;
      foreach (var source in sources)
      {
        if (string.CompareOrdinal(source.Id, resumePartition) < 0) continue;
        FoldPartition(source, null, source.Id == resumePartition ? resumeKey : default, save: true);
      }
    }
    if (RepairDays.Count > 0)
    {
      // Removes orphaned copies of interrupted writes, then copies the days again. A flush
      // writes the row before its copy, so a copy deleted here is copied again below.
      foreach (var (granularity, store) in stores)
      {
        if (RepairDaysOf(granularity) is not { } days) continue;
        foreach (var partition in store.ListPartitions())
        {
          using var lease = store.Acquire(partition);
          if (lease == null) continue;
          IndexRecoveryScan.Visit(lease.Tree, days, RollupIndexKeyCodec.HeadLength, (key, _) => lease.Tree.ForceDelete(key));
        }
      }
      foreach (var source in sources)
      {
        if (RepairDaysOf(source.Granularity) is { } days)
          FoldPartition(source, days, default, save: false);
      }
    }
    // Merges the copies into disk segments, as ingestion goes on.
    foreach (var store in AllStores) store.Compact();
    Complete(checkpoint);
    build.Complete($"indexes={indexes.Count}; physical stores={stores.Count}; source partitions={sourcePartitions}; rollup rows scanned={count}; resumed rows={resumed}; indexed source rows={matched}; index rows written={rows}; historical backfill={historical}", count - resumed, "rows");
  }

  /// <summary>The days of a tier to repair, or null.</summary>
  long[] RepairDaysOf(Granularity granularity) =>
      recovery.TryGetValue(granularity, out var journal) ? RepairDays.GetValueOrDefault(journal) : null;

  static string PartitionsKey(Granularity granularity) => "rollup-index.partitions." + granularity;

  string TierDirectory(Granularity granularity) => Path.Combine(Root, granularity.ToString().ToLowerInvariant());

  PartitionedStore<Memory<byte>, RollupValue> Open(Granularity granularity)
  {
    var store = new PartitionedStore<Memory<byte>, RollupValue>(TierDirectory(granularity), options.Storage.RollupIndexes.RecordsPerPartition,
        directory => factory.OpenRollupIndex(directory, granularity), Logger, factory.CreateRollupIndexMaintainer, factory.WriteAheadLogDirectoryOf);
    Resources.Add(store);
    return store;
  }

  public void Copy(Granularity granularity, Memory<byte> key, RollupValue value)
  {
    if (!IsHealthy || indexes.Count == 0) return;
    try { CopyCore(granularity, key, value); }
    catch (Exception exception)
    {
      Invalidate();
      // Primary rollups remain authoritative and must keep accepting deltas.
      try { Logger.LogError(exception); } catch { }
    }
  }

  public void BeforeWrite(Granularity granularity, Memory<byte> key)
  {
    if (!recovery.TryGetValue(granularity, out var journal)) return;
    var definition = KeyCodec.ReadDimensionSetId(key.Span);
    if (copies.ContainsKey((granularity, definition))) journal.Track(KeyCodec.ReadRollupBucket(key.Span));
  }

  void CopyCore(Granularity granularity, Memory<byte> originalKey, RollupValue value)
  {
    // Ordinary rollup rows do not need dimension arrays/dictionaries merely to discover
    // that their combination is not indexed.
    var definitionId = KeyCodec.ReadDimensionSetId(originalKey.Span);
    if (!copies.TryGetValue((granularity, definitionId), out var destinations)) return;
    var key = StorageKeyCodec.DecodeRollup(originalKey.Span);
    var store = destinations[0].Store;
    using var lease = store.Acquire(store.RouteWrite(key.BucketTicks));
    if (lease == null) return;
    foreach (var index in destinations)
    {
      var match = KeyCodec.Stored(key.Dimensions[index.Dimension]);
      lease.Tree.Upsert(RollupIndexKeyCodec.Key(index.KeyPrefix, key.Site, key.Event, match, key.BucketTicks, originalKey.Span), value);
    }
  }

  /// <summary>
  /// The indexed dimension a rollup read would use while the indexes are being built, or null:
  /// the dimension of its first mandatory equality filter with an index of the definition.
  /// </summary>
  public string Unbuilt(Granularity granularity, string definitionId, IEnumerable<Filter> filters)
  {
    if (!IsBuilding) return null;
    foreach (var filter in DimensionIndexStore.Mandatory(filters))
    {
      if (DimensionIndexStore.TryEquality(filter, out var dimension, out _) && indexes.Any(index =>
          index.Dimension == dimension && index.DefinitionId == definitionId && index.Granularity == granularity))
        return dimension;
    }
    return null;
  }

  public string Scan(Granularity granularity, string site, string eventName, string definitionId, long from, long to,
      IEnumerable<Filter> filters, Action<long, string, RollupValue> visit, CancellationToken cancellationToken)
  {
    if (!IsHealthy || !IsReady || from >= to) return null;
    foreach (var filter in DimensionIndexStore.Mandatory(filters))
    {
      if (!DimensionIndexStore.TryEquality(filter, out var dimension, out var values)) continue;
      var index = indexes.FirstOrDefault(index => index.Dimension == dimension && index.DefinitionId == definitionId && index.Granularity == granularity);
      if (index == null) continue;
      values = values.Distinct(StringComparer.Ordinal).ToArray();
      if (values.Length > 64) continue;
      var currentRevision = Revision;
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
    if (!IsHealthy || expectedRevision != Revision)
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
}
