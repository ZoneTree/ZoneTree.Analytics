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
/// days are reconciled at the next open, before workers start.
/// </summary>
sealed class DimensionIndexStore : IDisposable
{
  const string DimensionsKey = "dimension-index.dimensions";
  const string CleanKey = "dimension-index.clean";
  const string PartitionsKey = "dimension-index.partitions.shared";
  const int ProbeLimit = 1024;
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

  public IReadOnlyList<DimensionIndex> Indexes => indexes;
  public PartitionedStore<Memory<byte>, Memory<byte>> Store { get; private set; }
  public IEnumerable<PartitionedStore<Memory<byte>, Memory<byte>>> AllStores => Store == null ? [] : [Store];

  public bool IsAvailable => Volatile.Read(ref invalid) == 0 && Volatile.Read(ref suspended) == 0;
  public bool IsHealthy => Volatile.Read(ref invalid) == 0;

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

  public void Initialize()
  {
    using var startup = new StartupLog(logger, "Dimension index initialization", directory);
    using var check = new StartupLog(logger, "Dimension index backfill check", directory);
    var format = meta.Get(StorageFormats.DimensionIndexKey);
    if (format != null)
      StorageFormats.Require(format, StorageFormats.DimensionIndex, "The dimension indexes of the analytics data directory");
    var reasons = new List<string>();
    // Indexes that were never initialized have no checkpoint, manifest or format to compare.
    if (format == null) reasons.Add("new indexes");
    else
    {
      if (meta.Get(CleanKey) != "true") reasons.Add("missing or unclean checkpoint");
      if (meta.Get(DimensionsKey) != manifest) reasons.Add("configuration changed");
    }
    if (!factory.RawEventsDurable || !factory.DimensionIndexesDurable) reasons.Add("write-ahead logging disabled");
    var dirtyDays = recovery.Days;
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
    var rebuild = reasons.Count > 0;
    var decision = indexes.Count == 0 ? "disabled" : rebuild ? "rebuild required" : dirtyDays.Length > 0 ? "repair affected days" : "reuse existing indexes";
    var reason = indexes.Count == 0 ? "no configured dimensions" : rebuild ? string.Join(", ", reasons) : dirtyDays.Length > 0 ? "durable write intents require reconciliation" : "clean checkpoint and matching manifests";
    check.Complete($"decision={decision}; indexes={indexes.Count}; physical stores={(Store == null ? 0 : 1)}; partitions={partitions}; dirty days={dirtyDays.Length}; reasons={reason}");
    // Configuration changes rebuild the shared tree, removing discarded namespaces.
    // Disabling every dimension drops its partitions; interrupted cleanup is retried.
    if (Store == null && Directory.Exists(directory))
    {
      var obsolete = Open();
      foreach (var partition in obsolete.ListPartitions()) obsolete.Drop(partition);
    }
    if ((rebuild || dirtyDays.Length > 0) && indexes.Count > 0)
      Backfill(rawEvents, rebuild ? null : dirtyDays);
    MarkClean();
    startup.Complete($"decision={(rebuild && indexes.Count > 0 ? "rebuild completed" : dirtyDays.Length > 0 && indexes.Count > 0 ? "repair completed" : decision)}; indexes={indexes.Count}; physical stores={(Store == null ? 0 : 1)}");
  }

  PartitionedStore<Memory<byte>, Memory<byte>> Open()
  {
    var store = new PartitionedStore<Memory<byte>, Memory<byte>>(directory,
        options.RecordsPerPartition, factory.OpenDimensionIndex, logger,
        factory.CreateDimensionIndexMaintainer, factory.WriteAheadLogDirectoryOf);
    resources.Add(store);
    return store;
  }

  void Backfill(RawEventStore rawEvents, long[] days)
  {
    using var build = new StartupLog(logger, "Dimension index backfill", directory);
    var source = rawEvents.Store.ListPartitions();
    var checkpoint = new IndexBackfillCheckpoint(meta, "dimension-index.backfill",
        manifest + "\n" + string.Join(',', source) + "\n" + (days == null ? "all" : string.Join(',', days)));
    if (days == null) meta.Set(CleanKey, "false");
    if (!checkpoint.Resuming) checkpoint.Begin();
    if (!checkpoint.Copying)
    {
      if (days == null)
        foreach (var partition in Store.ListPartitions()) Store.Drop(partition);
      else
        foreach (var partition in Store.ListPartitions())
        {
          using var lease = Store.Acquire(partition);
          if (lease == null) continue;
          IndexRecoveryScan.Visit(lease.Tree, days, key => key.Length - 16, (key, _) => lease.Tree.ForceDelete(key));
        }
      IndexRecoveryScan.Persist(AllStores);
      checkpoint.Cleared();
    }
    var record = new RawEvent();
    var selector = new DimensionSelector(options.Dimensions);
    long records = checkpoint.Count;
    var resumed = records;
    long rows = 0;
    long bytes = 0;
    long skipped = 0;
    var partitions = 0;
    using var writer = CreateWriter();
    var resumePartition = checkpoint.Partition;
    var resumeKey = checkpoint.Key;
    foreach (var partition in source)
    {
      if (string.CompareOrdinal(partition, resumePartition) < 0) continue;
      using var lease = rawEvents.Store.Acquire(partition);
      if (lease == null) continue;
      partitions++;
      Memory<byte> lastKey = default;
      IndexRecoveryScan.Visit(lease.Tree, days, key => KeyCodec.HeadLength(key, 1), (key, value) =>
      {
        lastKey = key;
        var rawKey = StorageKeyCodec.DecodeRawEvent(key.Span);
        if (!RawEventCodec.TryDecode(value.Span, rawKey.Ticks, null, default, record, selector)) { skipped++; return; }
        var copies = writer.Write(rawKey.Site, rawKey.Ticks, rawKey.Sequence, record.Dimensions, value);
        rows += copies;
        bytes += (long)copies * value.Length;
        if (++records % IndexBackfillCheckpoint.Interval == 0)
        {
          writer.Dispose();
          IndexRecoveryScan.Persist(AllStores);
          checkpoint.Save(partition, lastKey, records);
          build.Progress($"checkpoint saved; events={records}; resumed events={resumed}; index rows written={rows}; payload bytes copied={bytes}", records - resumed, "events");
        }
      }, partition == resumePartition ? resumeKey : default);
      writer.Dispose();
      IndexRecoveryScan.Persist(AllStores);
      checkpoint.Save(partition, lastKey.IsEmpty && partition == resumePartition ? resumeKey : lastKey, records);
      build.Progress($"checkpoint saved; partition={partition}; events={records}; resumed events={resumed}", records - resumed, "events");
    }
    writer.Dispose();
    Store.Compact();
    Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    build.Complete($"indexes={indexes.Count}; physical stores=1; raw partitions={partitions}; events={records}; resumed events={resumed}; dirty days={(days == null ? 0 : days.Length)}; skipped events={skipped}; index rows written={rows}; payload bytes copied={bytes}", records - resumed, "events");
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

  public void MarkClean()
  {
    if (Volatile.Read(ref invalid) != 0) return;
    meta.Update(entries =>
    {
      entries[DimensionsKey] = manifest;
      entries[StorageFormats.DimensionIndexKey] = StorageFormats.Text(StorageFormats.DimensionIndex);
      entries[PartitionsKey] = Store == null ? "" : string.Join(',', Store.ListPartitions());
      entries[CleanKey] = factory.RawEventsDurable && factory.DimensionIndexesDurable ? "true" : "false";
      recovery.Clear(entries);
      new IndexBackfillCheckpoint(meta, "dimension-index.backfill", manifest).Clear(entries);
    });
  }

  public void Dispose() => Disposal.DisposeAll(resources);
}
