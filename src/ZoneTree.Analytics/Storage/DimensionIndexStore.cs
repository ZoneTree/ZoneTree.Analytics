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
/// the indexes are ready. Erasure and the build's chunks hold <see cref="Gate"/> one at a time,
/// and every chunk reads the raw events again, so a build never copies an erased event back.
/// </remarks>
sealed class DimensionIndexStore : BackgroundIndex
{
  const string PartitionsKey = "dimension-index.partitions.shared";
  const int ProbeLimit = 1024;
  const int ChunkSize = 4096;
  readonly DimensionIndexOptions options;
  readonly RawEventStore rawEvents;
  readonly StoreFactory factory;
  readonly IndexRecovery recovery;
  readonly List<DimensionIndex> indexes = [];
  int suspended;
  long fence;

  public IReadOnlyList<DimensionIndex> Indexes => indexes;
  public PartitionedStore<Memory<byte>, Memory<byte>> Store { get; private set; }
  public IEnumerable<PartitionedStore<Memory<byte>, Memory<byte>>> AllStores => Store == null ? [] : [Store];

  public bool IsAvailable => IsReady && IsHealthy && Volatile.Read(ref suspended) == 0;

  /// <summary>Held by erasure and by each chunk of a build, so that a build never copies an erased event.</summary>
  public object Gate { get; } = new();

  public DimensionIndexStore(string directory, DimensionIndexOptions options, RawEventStore rawEvents,
      StoreFactory factory, MetaStore meta, ILogger logger)
      : base(meta, logger, directory, "Dimension index", "dimension-index", "dimension-index.dimensions",
          StorageFormats.DimensionIndexKey, StorageFormats.DimensionIndex, "The dimension indexes of the analytics data directory")
  {
    this.options = options;
    this.rawEvents = rawEvents;
    this.factory = factory;
    recovery = new(meta, "dimension-index.dirty.");
  }

  /// <summary>
  /// Opens the indexes and decides their work. Builds copy the raw events up to
  /// <paramref name="fence"/>, the last sequence stored before ingestion starts: ingestion copies
  /// the later ones.
  /// </summary>
  public void Initialize(long fence)
  {
    this.fence = fence;
    Open(string.Join('\n', options.Dimensions.Order(StringComparer.Ordinal)), []);
  }

  protected override int IndexCount => indexes.Count;

  protected override int StoreCount => Store == null ? 0 : 1;

  protected override string DisabledReason => "no configured dimensions";

  protected override bool Durable => factory.RawEventsDurable && factory.DimensionIndexesDurable;

  protected override IEnumerable<IndexRecovery> Journals => [recovery];

  protected override bool NothingToCopy => rawEvents.Store.ListPartitions().Count == 0;

  protected override int OpenStores(List<string> reasons)
  {
    if (options.Dimensions.Count == 0)
    {
      // Disabling every dimension drops its partitions; interrupted cleanup is retried.
      if (Directory.Exists(Root))
      {
        var obsolete = Open();
        foreach (var partition in obsolete.ListPartitions()) obsolete.Drop(partition);
      }
      return 0;
    }
    // One shared tree holds every dimension: a configuration change rebuilds it, removing
    // discarded namespaces.
    Store = Open();
    foreach (var dimension in options.Dimensions) indexes.Add(new(dimension, Store));
    var names = Store.ListPartitions();
    var expected = (Meta.Get(PartitionsKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
    if (IndexRecoveryScan.MissingRetainedPartitions(expected, names, factory.RawRetentionCutoff))
      reasons.Add("partition manifest changed");
    return names.Count;
  }

  protected override void DropCopies()
  {
    foreach (var partition in Store.ListPartitions()) Store.Drop(partition);
  }

  protected override void SavePartitions(Dictionary<string, string> entries) =>
      entries[PartitionsKey] = Store == null ? "" : string.Join(',', Store.ListPartitions());

  protected override AnalyticsStorageException BuildFailed(Exception cause) => new(
      "The dimension indexes could not be built" + (cause == null ? ": a copy failed." : ": " + cause.Message) +
      " Queries scan raw events without them until the engine opens again, which resumes the build.", cause);

  /// <summary>
  /// Runs the build or repair that <see cref="Initialize"/> decided, on the background
  /// maintenance thread, then makes the indexes available to queries. Every attempt resumes
  /// from the checkpoint, so a failed attempt can simply be repeated.
  /// </summary>
  public override void Build(CancellationToken cancellationToken)
  {
    if (IsReady) return;
    // Indexes that failed wait for the next open, which resumes them from their last checkpoint.
    RequireHealthy();
    using var build = new StageLog(Logger, "Dimension index backfill", Root);
    var checkpoint = Checkpoint();
    var repairDays = RepairDays.GetValueOrDefault(recovery) ?? [];
    var record = new RawEvent();
    var selector = new DimensionSelector(options.Dimensions);
    long records = Rebuilding ? checkpoint.Count : 0;
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
        throw BuildFailed(exception);
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
    if (Rebuilding)
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
    var store = new PartitionedStore<Memory<byte>, Memory<byte>>(Root,
        options.RecordsPerPartition, factory.OpenDimensionIndex, Logger,
        factory.CreateDimensionIndexMaintainer, factory.WriteAheadLogDirectoryOf);
    Resources.Add(store);
    return store;
  }

  public void BeforeWrite(long ticks)
  {
    if (indexes.Count > 0) recovery.Track(ticks);
  }

  public DimensionIndexWriter CreateWriter() => new(this);

  public IDisposable Suspend()
  {
    Interlocked.Increment(ref suspended);
    ChangeRevision();
    return new Suspension(this);
  }

  sealed class Suspension(DimensionIndexStore owner) : IDisposable
  {
    DimensionIndexStore current = owner;
    public void Dispose()
    {
      var released = Interlocked.Exchange(ref current, null);
      if (released == null) return;
      released.ChangeRevision();
      Interlocked.Decrement(ref released.suspended);
    }
  }

  public void Check(DimensionIndexSelection selection)
  {
    if (!CanUse(selection))
      throw new DimensionIndexUnavailableException();
  }

  public bool CanUse(DimensionIndexSelection selection) =>
      IsAvailable && selection.Revision == Revision;

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
    var currentRevision = Revision;
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
    return IsAvailable && currentRevision == Revision ? best : null;
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
}
