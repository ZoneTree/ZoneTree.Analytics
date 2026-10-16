using ZoneTree.Analytics.Internal;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

sealed record DimensionIndex(string Dimension, PartitionedStore<Memory<byte>, Memory<byte>> Store)
{
  public byte[] KeyPrefix { get; } = DimensionIndexKeyCodec.Namespace(Dimension);
}

sealed record DimensionIndexSelection(DimensionIndex Index, string[] Values, long Revision);

sealed class DimensionIndexUnavailableException : Exception;

/// <summary>
/// Owns optional covering indexes. The primary raw store is authoritative. A durable dirty
/// intent is saved before writes. Completed builds survive abrupt shutdown; only affected
/// days are reconciled after the next open.
/// </summary>
/// <remarks>
/// Builds and repairs run on the background maintenance thread while ingestion writes its own
/// copies: copying an event again writes the same key and value. Queries scan raw events until
/// the indexes are ready. A build resumes from its checkpoint after a restart; the days that
/// ingestion wrote meanwhile are repaired. Erasure and the build's chunks hold <see cref="Gate"/>
/// one at a time, and every chunk reads the raw events again, so a build never copies an
/// erased event back.
/// </remarks>
sealed class DimensionIndexStore : IDisposable
{
  const string DimensionsKey = "dimension-index.dimensions";
  const string CleanKey = "dimension-index.clean";
  const string PartitionsKey = "dimension-index.partitions.shared";
  const string UncleanReason = "missing or unclean checkpoint";
  const int ProbeLimit = 1024;
  const int ChunkSize = 4096;
  readonly MetaStore meta;
  readonly string manifest;
  readonly string directory;
  readonly DimensionIndexOptions options;
  readonly RawEventStore rawEvents;
  readonly StoreFactory factory;
  readonly ILogger logger;
  readonly IndexRecovery recovery;
  readonly List<DimensionIndex> indexes = [];
  readonly List<IDisposable> resources = [];
  int invalid;
  int suspended;
  long revision;
  volatile bool ready;
  bool rebuild;
  long fence;
  long[] dirtyDays = [];
  long[] repairDays = [];

  public IReadOnlyList<DimensionIndex> Indexes => indexes;
  public PartitionedStore<Memory<byte>, Memory<byte>> Store { get; private set; }
  public IEnumerable<PartitionedStore<Memory<byte>, Memory<byte>>> AllStores => Store == null ? [] : [Store];

  public bool IsAvailable => ready && Volatile.Read(ref invalid) == 0 && Volatile.Read(ref suspended) == 0;
  public bool IsHealthy => Volatile.Read(ref invalid) == 0;

  /// <summary>Whether the indexes are being built or repaired; queries scan raw events meanwhile.</summary>
  public bool IsBuilding => indexes.Count > 0 && !ready && IsHealthy;

  /// <summary>Held by erasure and by each chunk of a build, so that a build never copies an erased event.</summary>
  public object Gate { get; } = new();

  public DimensionIndexStore(string directory, DimensionIndexOptions options, RawEventStore rawEvents,
      StoreFactory factory, MetaStore meta, ILogger logger)
  {
    this.meta = meta;
    this.directory = directory;
    this.options = options;
    this.rawEvents = rawEvents;
    this.factory = factory;
    this.logger = logger;
    recovery = new(meta, "dimension-index.dirty.");
    manifest = string.Join('\n', options.Dimensions.Order(StringComparer.Ordinal));
  }

  /// <summary>
  /// Opens the indexes and decides whether they need a build or a repair, which
  /// <see cref="Build"/> then runs in the background. A new build drops the old copies first,
  /// before ingestion writes new ones. Builds copy the raw events up to <paramref name="fence"/>,
  /// the last sequence stored before ingestion starts: ingestion copies the later ones.
  /// </summary>
  public void Initialize(long fence)
  {
    this.fence = fence;
    using var startup = new StageLog(logger, "Dimension index initialization", directory);
    using var check = new StageLog(logger, "Dimension index backfill check", directory);
    var format = meta.Get(StorageFormats.DimensionIndexKey);
    if (format != null)
      StorageFormats.Require(format, StorageFormats.DimensionIndex, "The dimension indexes of the analytics data directory");
    var reasons = new List<string>();
    // Indexes that were never initialized have no checkpoint, manifest or format to compare.
    if (format == null) reasons.Add("new indexes");
    else
    {
      if (meta.Get(CleanKey) != "true") reasons.Add(UncleanReason);
      if (meta.Get(DimensionsKey) != manifest) reasons.Add("configuration changed");
    }
    if (!factory.RawEventsDurable || !factory.DimensionIndexesDurable) reasons.Add("write-ahead logging disabled");
    dirtyDays = recovery.Days;
    var partitions = 0;
    if (options.Dimensions.Count > 0)
    {
      Store = Open();
      foreach (var dimension in options.Dimensions) indexes.Add(new(dimension, Store));
      var names = Store.ListPartitions();
      partitions = names.Count;
      var expected = (meta.Get(PartitionsKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
      if (IndexRecoveryScan.MissingRetainedPartitions(expected, names, factory.RawRetentionCutoff))
        reasons.Add("partition manifest changed");
    }
    rebuild = reasons.Count > 0 && indexes.Count > 0;
    var checkpoint = Checkpoint();
    // Only an interrupted build of the same configuration resumes; any other reason starts over.
    var resuming = rebuild && checkpoint.Copying && reasons.All(reason => reason == UncleanReason);
    // A new build copies every day; one that resumes repairs the days written since it began.
    repairDays = rebuild && !resuming ? [] : dirtyDays;
    var decision = indexes.Count == 0 ? "disabled" : rebuild ? "rebuild required" : dirtyDays.Length > 0 ? "repair affected days" : "reuse existing indexes";
    var reason = indexes.Count == 0 ? "no configured dimensions" : rebuild ? string.Join(", ", reasons) : dirtyDays.Length > 0 ? "durable write intents require reconciliation" : "clean checkpoint and matching manifests";
    check.Complete($"decision={decision}; indexes={indexes.Count}; physical stores={(Store == null ? 0 : 1)}; partitions={partitions}; dirty days={dirtyDays.Length}; resuming={resuming}; reasons={reason}");
    // Configuration changes rebuild the shared tree, removing discarded namespaces.
    // Disabling every dimension drops its partitions; interrupted cleanup is retried.
    if (Store == null && Directory.Exists(directory))
    {
      var obsolete = Open();
      foreach (var partition in obsolete.ListPartitions()) obsolete.Drop(partition);
    }
    if (rebuild && !resuming)
    {
      meta.Set(CleanKey, "false");
      checkpoint.Begin();
      foreach (var partition in Store.ListPartitions()) Store.Drop(partition);
      Started(checkpoint);
      // A new directory has nothing to copy: the build completes at once.
      if (rawEvents.Store.ListPartitions().Count == 0)
        Build(CancellationToken.None);
    }
    if (!rebuild && repairDays.Length == 0)
    {
      ready = true;
      MarkClean();
    }
    startup.Complete($"decision={(ready ? rebuild ? "rebuild completed" : decision : rebuild ? "rebuild in background" : "repair in background")}; indexes={indexes.Count}; physical stores={(Store == null ? 0 : 1)}");
  }

  IndexBackfillCheckpoint Checkpoint() => new(meta, "dimension-index.backfill", manifest);

  /// <summary>
  /// Stops a build once a copy has failed: the copies after it were skipped, so no checkpoint
  /// or coverage may claim them.
  /// </summary>
  void RequireHealthy()
  {
    if (!IsHealthy)
      throw Failed(null);
  }

  /// <summary>The failure of a build that cannot succeed until the engine opens again.</summary>
  static AnalyticsStorageException Failed(Exception cause) => new(
      "The dimension indexes could not be built" + (cause == null ? ": a copy failed." : ": " + cause.Message) +
      " Queries scan raw events without them until the engine opens again, which resumes the build.", cause);

  /// <summary>Records that the indexes cover the raw events, and makes them available to queries.</summary>
  void Complete(IndexBackfillCheckpoint checkpoint)
  {
    RequireHealthy();
    meta.Update(entries =>
    {
      SaveCoverage(entries);
      checkpoint.Clear(entries);
    });
    recovery.ClearRepaired(dirtyDays);
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
  /// maintenance thread, then makes the indexes available to queries. Every attempt resumes
  /// from the checkpoint, so a failed attempt can simply be repeated.
  /// </summary>
  public void Build(CancellationToken cancellationToken)
  {
    if (ready) return;
    // Indexes that failed wait for the next open, which resumes them from their last checkpoint.
    RequireHealthy();
    using var build = new StageLog(logger, "Dimension index backfill", directory);
    var checkpoint = Checkpoint();
    var record = new RawEvent();
    var selector = new DimensionSelector(options.Dimensions);
    long records = rebuild ? checkpoint.Count : 0;
    var resumed = records;
    long rows = 0;
    long bytes = 0;
    long skipped = 0;
    var partitions = 0;
    using var writer = CreateWriter();
    Memory<byte> lastKey = default;
    void Copy(Memory<byte> key, Memory<byte> value)
    {
      lastKey = key;
      ++records;
      // Ingestion has copied the events it stored since the engine opened.
      if (KeyCodec.ReadRawSequence(key.Span) > fence) return;
      var rawKey = StorageKeyCodec.DecodeRawEvent(key.Span);
      if (!RawEventCodec.TryDecode(value.Span, rawKey.Ticks, null, default, record, selector)) { skipped++; return; }
      int copies;
      try
      {
        copies = writer.Write(rawKey.Site, rawKey.Ticks, rawKey.Sequence, record.Dimensions, value);
      }
      catch (Exception exception)
      {
        // The writer has invalidated the indexes: no retry in this run can complete them.
        throw Failed(exception);
      }
      rows += copies;
      bytes += (long)copies * value.Length;
    }
    // Copies the events of a partition from a key on, a chunk at a time, and saves a checkpoint
    // every interval of events.
    void CopyPartition(PartitionLease<Memory<byte>, Memory<byte>> lease, long[] days, Memory<byte> after, Action<Memory<byte>> save)
    {
      var next = (records / IndexBackfillCheckpoint.Interval + 1) * IndexBackfillCheckpoint.Interval;
      do
      {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
          after = IndexRecoveryScan.Visit(lease.Tree, days, KeyCodec.RawEventHead, Copy, after, (int)Math.Min(ChunkSize, next - records));
        if (records == next && !after.IsEmpty)
        {
          save?.Invoke(after);
          next += IndexBackfillCheckpoint.Interval;
        }
      } while (!after.IsEmpty);
    }
    void Save(string partition, Memory<byte> key)
    {
      RequireHealthy();
      checkpoint.Save(partition, key, records);
    }
    if (rebuild)
    {
      var resumePartition = checkpoint.Partition;
      var resumeKey = checkpoint.Key;
      // Events stored later have copies from ingestion: the partitions of now hold every event to copy.
      foreach (var partition in rawEvents.Store.ListPartitions())
      {
        if (string.CompareOrdinal(partition, resumePartition) < 0) continue;
        using var lease = rawEvents.Store.Acquire(partition);
        if (lease == null) continue;
        partitions++;
        lastKey = partition == resumePartition ? resumeKey : default;
        CopyPartition(lease, null, lastKey, key =>
        {
          Save(partition, key);
          build.Progress($"checkpoint saved; events={records}; resumed events={resumed}; index rows written={rows}; payload bytes copied={bytes}", records - resumed, "events");
        });
        Save(partition, lastKey);
        build.Progress($"checkpoint saved; partition={partition}; events={records}; resumed events={resumed}", records - resumed, "events");
      }
    }
    if (repairDays.Length > 0)
    {
      // Removes the copies of the days that were stored before the engine opened, orphans of
      // interrupted deletions included, then copies the days again up to the same fence.
      foreach (var partition in Store.ListPartitions())
      {
        using var lease = Store.Acquire(partition);
        if (lease == null) continue;
        IndexRecoveryScan.Visit(lease.Tree, repairDays, DimensionIndexKeyCodec.HeadLength, (key, _) =>
        {
          if (DimensionIndexKeyCodec.Sequence(key.Span) <= fence)
            lease.Tree.ForceDelete(key);
        });
      }
      foreach (var partition in rawEvents.Store.ListPartitions())
      {
        using var lease = rawEvents.Store.Acquire(partition);
        if (lease == null) continue;
        partitions++;
        CopyPartition(lease, repairDays, default, null);
        build.Progress($"partition repaired; partition={partition}; events={records}; dirty days={repairDays.Length}", records, "events");
      }
    }
    writer.Dispose();
    // Merges the copies into disk segments, as ingestion goes on.
    Store.Compact();
    Complete(checkpoint);
    build.Complete($"indexes={indexes.Count}; physical stores=1; raw partitions={partitions}; events={records}; resumed events={resumed}; dirty days={repairDays.Length}; skipped events={skipped}; index rows written={rows}; payload bytes copied={bytes}", records - resumed, "events");
  }

  PartitionedStore<Memory<byte>, Memory<byte>> Open()
  {
    var store = new PartitionedStore<Memory<byte>, Memory<byte>>(directory,
        options.RecordsPerPartition, factory.OpenDimensionIndex, logger,
        factory.CreateDimensionIndexMaintainer, factory.WriteAheadLogDirectoryOf);
    resources.Add(store);
    return store;
  }

  public void BeforeWrite(long ticks)
  {
    if (indexes.Count > 0) recovery.Track(ticks);
  }

  public DimensionIndexWriter CreateWriter() => new(this);

  public void Invalidate()
  {
    Volatile.Write(ref invalid, 1);
    Interlocked.Increment(ref revision);
  }

  public IDisposable Suspend()
  {
    Interlocked.Increment(ref suspended);
    Interlocked.Increment(ref revision);
    return new Suspension(this);
  }

  sealed class Suspension(DimensionIndexStore owner) : IDisposable
  {
    DimensionIndexStore current = owner;
    public void Dispose()
    {
      var released = Interlocked.Exchange(ref current, null);
      if (released == null) return;
      Interlocked.Increment(ref released.revision);
      Interlocked.Decrement(ref released.suspended);
    }
  }

  public void Check(DimensionIndexSelection selection)
  {
    if (!IsAvailable || selection.Revision != Interlocked.Read(ref revision))
      throw new DimensionIndexUnavailableException();
  }

  public bool CanUse(DimensionIndexSelection selection) =>
      IsAvailable && selection.Revision == Interlocked.Read(ref revision);

  /// <summary>The query plan notice of a scan that could not read the index of a dimension yet.</summary>
  public static string UnbuiltNotice(string dimension) =>
      $"The dimension index on '{dimension}' is being built in the background; raw events were scanned without it.";

  /// <summary>
  /// The indexed dimension a query would read while the indexes are being built, or null: the
  /// dimension of its first mandatory equality filter on an indexed dimension.
  /// </summary>
  public string Unbuilt(IEnumerable<Filter> filters, Func<string, bool> allowed)
  {
    if (!IsBuilding) return null;
    foreach (var filter in Mandatory(filters))
    {
      if (TryEquality(filter, out var dimension, out _) && allowed(dimension) && indexes.Any(index => index.Dimension == dimension))
        return dimension;
    }
    return null;
  }

  /// <summary>
  /// Selects a mandatory, case-sensitive equality predicate. AND children are safe; an OR
  /// can only be used when every branch is equality on the same indexed dimension.
  /// Bounded prefix probes favor small match sets without scanning a large candidate twice.
  /// </summary>
  public DimensionIndexSelection Select(string site, long from, long to, IEnumerable<Filter> filters,
      Func<string, bool> allowed, CancellationToken cancellationToken)
  {
    if (indexes.Count == 0 || !IsAvailable) return null;
    var currentRevision = Interlocked.Read(ref revision);
    DimensionIndexSelection best = null;
    var bestCount = int.MaxValue;
    foreach (var filter in Mandatory(filters))
    {
      if (!TryEquality(filter, out var dimension, out var values) || !allowed(dimension)) continue;
      var index = indexes.FirstOrDefault(x => x.Dimension == dimension);
      if (index == null) continue;
      var selection = new DimensionIndexSelection(index, values.Distinct(StringComparer.Ordinal).ToArray(), currentRevision);
      if (selection.Values.Length > 64) continue;
      var count = DimensionIndexScan.Count(site, from, to, selection, ProbeLimit, cancellationToken);
      if (count < bestCount) { best = selection; bestCount = count; }
      if (count == 0) break;
    }
    return IsAvailable && currentRevision == Interlocked.Read(ref revision) ? best : null;
  }

  internal static IEnumerable<Filter> Mandatory(IEnumerable<Filter> filters)
  {
    foreach (var filter in filters)
    {
      if (filter.Operator == FilterOperator.AllOf)
        foreach (var child in Mandatory(filter.Children)) yield return child;
      else yield return filter;
    }
  }

  internal static bool TryEquality(Filter filter, out string dimension, out string[] values)
  {
    dimension = filter.Dimension;
    values = [];
    if (filter.Operator == FilterOperator.Equal && !filter.IgnoreCase)
    {
      values = [.. filter.Values];
      return true;
    }
    if (filter.Operator != FilterOperator.AnyOf) return false;
    var combined = new List<string>();
    foreach (var child in filter.Children)
    {
      if (!TryEquality(child, out var name, out var matches)) return false;
      dimension ??= name;
      if (dimension != name) return false;
      combined.AddRange(matches);
    }
    values = [.. combined];
    return true;
  }

  /// <summary>
  /// Records that the indexes cover the raw events, when they do: after an orderly shutdown no
  /// day needs a repair. An unfinished build keeps its checkpoint and resumes at the next open.
  /// </summary>
  public void MarkClean()
  {
    if (!ready || Volatile.Read(ref invalid) != 0) return;
    meta.Update(entries =>
    {
      SaveCoverage(entries);
      recovery.Clear(entries);
      Checkpoint().Clear(entries);
    });
  }

  void SaveCoverage(Dictionary<string, string> entries)
  {
    entries[DimensionsKey] = manifest;
    entries[StorageFormats.DimensionIndexKey] = StorageFormats.Text(StorageFormats.DimensionIndex);
    entries[PartitionsKey] = Store == null ? "" : string.Join(',', Store.ListPartitions());
    entries[CleanKey] = factory.RawEventsDurable && factory.DimensionIndexesDurable ? "true" : "false";
  }

  public void Dispose() => Disposal.DisposeAll(resources);
}
