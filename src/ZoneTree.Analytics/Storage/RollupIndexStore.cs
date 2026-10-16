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
/// copies. Queries read the primary rollups until the indexes are ready. A build resumes from
/// its checkpoint after a restart; the days that flushes wrote meanwhile are repaired.
/// </remarks>
sealed class RollupIndexStore : IDisposable
{
  const string CleanKey = "rollup-index.clean";

  /// <summary>The prefix of the build checkpoint, which a new history reconstruction clears.</summary>
  public const string CheckpointPrefix = "rollup-index.backfill";

  const string UncleanReason = "missing or unclean checkpoint";
  const string FoldingReason = "rebuilt history to fold in";
  const int BackgroundBatch = 4096;
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
  RollupActivations activations;
  volatile bool ready;
  bool rebuild;
  Dictionary<Granularity, long[]> dirtyDays = [];
  Dictionary<Granularity, long[]> repairDays = [];

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

  /// <summary>Whether the indexes are being built or repaired; queries read the primary rollups meanwhile.</summary>
  public bool IsBuilding => indexes.Count > 0 && !ready && IsHealthy;
  public IEnumerable<PartitionedStore<Memory<byte>, RollupValue>> AllStores => stores.Values;

  static string Registration(string dimension, string definitionId, Granularity granularity) =>
      string.Join(KeyCodec.ValueSeparator, dimension, definitionId, granularity.ToString());

  /// <summary>
  /// Opens the indexes and decides whether they need a build or a repair, which
  /// <see cref="Build"/> then runs in the background. A new build drops the old copies first,
  /// before ingestion writes new ones.
  /// </summary>
  public void Initialize(RollupActivations activations)
  {
    this.activations = activations;
    using var startup = new StageLog(logger, "Rollup index initialization", root);
    using var check = new StageLog(logger, "Rollup index backfill check", root);
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
      if (meta.Get(CleanKey) != "true") reasons.Add(UncleanReason);
      if (meta.Get("rollup-index.config") != manifest) reasons.Add("configuration changed");
    }
    if (!factory.RollupsDurable || !factory.RollupIndexesDurable) reasons.Add("write-ahead logging disabled");
    // Rows of a completed history reconstruction wait for this build to fold them in.
    if (JointRollupReconstruction.HasFolding(meta)) reasons.Add(FoldingReason);
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
    rebuild = reasons.Count > 0 && indexes.Count > 0;
    var checkpoint = Checkpoint();
    // Only an interrupted build of the same configuration resumes; any other reason starts over.
    var resuming = rebuild && checkpoint.Copying && reasons.All(reason => reason is UncleanReason or FoldingReason);
    dirtyDays = recovery.ToDictionary(pair => pair.Key, pair => pair.Value.Days);
    // A new build copies every day; one that resumes repairs the days written since it began.
    repairDays = rebuild && !resuming ? [] : dirtyDays.Where(pair => pair.Value.Length > 0).ToDictionary();
    var dirtyCount = dirtyDays.Values.Sum(days => days.Length);
    var decision = indexes.Count == 0 ? "disabled" : rebuild ? "rebuild required" : dirtyCount > 0 ? "repair affected days" : "reuse existing indexes unless historical rollups change";
    var reason = indexes.Count == 0 ? "no configured combinations or enabled tiers" : rebuild ? string.Join(", ", reasons) : dirtyCount > 0 ? "durable write intents require reconciliation" : "clean checkpoint and matching manifests";
    check.Complete($"decision={decision}; indexes={indexes.Count}; physical stores={stores.Count}; partitions={partitions}; dirty days={dirtyCount}; resuming={resuming}; reasons={reason}");
    // Disabled tiers must not retain copies. Only the three known tier directories are owned here.
    foreach (var granularity in RollupStore.Granularities.Where(granularity => !stores.ContainsKey(granularity)))
    {
      if (!Directory.Exists(TierDirectory(granularity))) continue;
      var obsolete = Open(granularity);
      foreach (var partition in obsolete.ListPartitions()) obsolete.Drop(partition);
    }
    if (rebuild && !resuming)
    {
      meta.Set(CleanKey, "false");
      checkpoint.Begin();
      foreach (var store in stores.Values)
        foreach (var partition in store.ListPartitions()) store.Drop(partition);
      Started(checkpoint);
      // A new directory has nothing to copy: the build completes at once.
      if (rollups.PrimaryStores.All(store => store.ListPartitions().Count == 0) && !JointRollupReconstruction.IsPending(meta))
        Build(CancellationToken.None);
    }
    if (!rebuild && repairDays.Count == 0)
    {
      ready = true;
      MarkClean();
    }
    startup.Complete($"decision={(ready ? indexes.Count == 0 ? "disabled" : rebuild ? "rebuild completed" : "reuse existing indexes" : rebuild ? "rebuild in background" : "repair in background")}; indexes={indexes.Count}; physical stores={stores.Count}");
  }

  IndexBackfillCheckpoint Checkpoint() => new(meta, CheckpointPrefix, manifest);

  /// <summary>
  /// Stops a build once a copy has failed: the copies after it were skipped, so no checkpoint,
  /// coverage or published history may claim them.
  /// </summary>
  void RequireHealthy()
  {
    if (!IsHealthy)
      throw new AnalyticsStorageException(
          "The rollup indexes could not be built: a copy failed. Queries read the primary rollups until the engine opens again, which resumes the build.");
  }

  /// <summary>
  /// Publishes the history the build folded in, records that the indexes copy the rollups, and
  /// makes them available to queries.
  /// </summary>
  void Complete(IndexBackfillCheckpoint checkpoint)
  {
    RequireHealthy();
    JointRollupReconstruction.PublishFolded(meta, activations, options.Rollups.Definitions);
    meta.Update(entries =>
    {
      SaveCoverage(entries);
      checkpoint.Clear(entries);
    });
    foreach (var (granularity, journal) in recovery) journal.ClearRepaired(dirtyDays[granularity]);
    ready = true;
    Interlocked.Increment(ref revision);
  }

  /// <summary>
  /// Records the configuration and format of a build that has dropped the old copies: an
  /// interrupted build of them resumes at the next open, while the indexes stay unclean.
  /// </summary>
  void Started(IndexBackfillCheckpoint checkpoint)
  {
    meta.Update(entries =>
    {
      SaveCoverage(entries);
      entries[CleanKey] = "false";
    });
    checkpoint.Cleared();
  }

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
  public void Build(CancellationToken cancellationToken)
  {
    if (ready) return;
    // Indexes that failed wait for the next open, which resumes them from their last checkpoint.
    RequireHealthy();
    using var build = new StageLog(logger, "Rollup index backfill", root);
    var checkpoint = Checkpoint();
    var historical = JointRollupReconstruction.HasFolding(meta);
    // Rebuilt rows wait for this fold even when their definition is no longer indexed.
    var folding = JointRollupReconstruction.FoldingDefinitions(meta, options.Rollups.Definitions);
    long count = rebuild ? checkpoint.Count : 0;
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
    if (rebuild)
    {
      var resumePartition = checkpoint.Partition;
      var resumeKey = checkpoint.Key;
      foreach (var source in sources)
      {
        if (string.CompareOrdinal(source.Id, resumePartition) < 0) continue;
        FoldPartition(source, null, source.Id == resumePartition ? resumeKey : default, save: true);
      }
    }
    if (repairDays.Count > 0)
    {
      // Removes orphaned copies of interrupted writes, then copies the days again. A flush
      // writes the row before its copy, so a copy deleted here is copied again below.
      foreach (var (granularity, store) in stores)
      {
        if (!repairDays.TryGetValue(granularity, out var days)) continue;
        foreach (var partition in store.ListPartitions())
        {
          using var lease = store.Acquire(partition);
          if (lease == null) continue;
          IndexRecoveryScan.Visit(lease.Tree, days, RollupIndexKeyCodec.HeadLength, (key, _) => lease.Tree.ForceDelete(key));
        }
      }
      foreach (var source in sources.Where(source => repairDays.ContainsKey(source.Granularity)))
        FoldPartition(source, repairDays[source.Granularity], default, save: false);
    }
    // Merges the copies into disk segments, as ingestion goes on.
    foreach (var store in AllStores) store.Compact();
    Complete(checkpoint);
    build.Complete($"indexes={indexes.Count}; physical stores={stores.Count}; source partitions={sourcePartitions}; rollup rows scanned={count}; resumed rows={resumed}; indexed source rows={matched}; index rows written={rows}; historical backfill={historical}", count - resumed, "rows");
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
    if (!IsHealthy || !ready || from >= to) return null;
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

  /// <summary>
  /// Records that the indexes copy the rollups, when they do: after an orderly shutdown no day
  /// needs a repair. An unfinished build keeps its checkpoint and resumes at the next open.
  /// </summary>
  public void MarkClean()
  {
    if (!IsHealthy || !ready) return;
    meta.Update(entries =>
    {
      SaveCoverage(entries);
      foreach (var journal in recovery.Values) journal.Clear(entries);
      Checkpoint().Clear(entries);
    });
  }

  void SaveCoverage(Dictionary<string, string> entries)
  {
    entries[CleanKey] = factory.RollupsDurable && factory.RollupIndexesDurable ? "true" : "false";
    entries[StorageFormats.RollupIndexKey] = StorageFormats.Text(StorageFormats.RollupIndex);
    entries["rollup-index.config"] = manifest;
    foreach (var (granularity, store) in stores) entries[PartitionsKey(granularity)] = string.Join(',', store.ListPartitions());
  }

  public void Dispose() => Disposal.DisposeAll(resources);
}
