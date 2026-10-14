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
/// marker is set before any writes; an unclean shutdown, configuration change or failed write
/// rebuilds indexes from retained raw events at the next open, before workers start.
/// </summary>
sealed class DimensionIndexStore : IDisposable
{
  const string DimensionsKey = "dimension-index.dimensions";
  const string CleanKey = "dimension-index.clean";
  const string Format = "2";
  const string PartitionsKey = "dimension-index.partitions.shared";
  const int ProbeLimit = 1024;
  readonly MetaStore meta;
  readonly string manifest;
  readonly string directory;
  readonly DimensionIndexOptions options;
  readonly RawEventStore rawEvents;
  readonly StoreFactory factory;
  readonly ILogger logger;
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
    manifest = string.Join('\n', options.Dimensions.Order(StringComparer.Ordinal));
  }

  public void Initialize()
  {
    using var startup = new StartupLog(logger, "Dimension index initialization", directory);
    using var check = new StartupLog(logger, "Dimension index backfill check", directory);
    if (meta.Get("dimension-index.format") is { } format && format != Format)
      throw new AnalyticsConfigurationException("Dimension indexes use a new shared storage format. Start with a fresh analytics data directory.");
    var reasons = new List<string>();
    if (meta.Get(CleanKey) != "true") reasons.Add("missing or unclean checkpoint");
    if (meta.Get(DimensionsKey) != manifest) reasons.Add("configuration changed");
    if (meta.Get("dimension-index.format") != Format) reasons.Add("format changed");
    // Includes engines opened with no indexes: disabling and re-enabling an index must
    // never reuse copies that missed the events ingested while it was disabled.
    meta.Update(entries =>
    {
      entries[CleanKey] = "false";
      entries["dimension-index.format"] = Format;
    });
    var partitions = 0;
    if (options.Dimensions.Count > 0)
    {
      Store = Open();
      foreach (var dimension in options.Dimensions) indexes.Add(new(dimension, Store));
      var names = Store.ListPartitions();
      partitions = names.Count;
      if (meta.Get(PartitionsKey) != string.Join(',', names)) reasons.Add("partition manifest changed");
    }
    var rebuild = reasons.Count > 0;
    var decision = indexes.Count == 0 ? "disabled" : rebuild ? "rebuild required" : "reuse existing indexes";
    var reason = indexes.Count == 0 ? "no configured dimensions" : rebuild ? string.Join(", ", reasons) : "clean checkpoint and matching manifests";
    check.Complete($"decision={decision}; indexes={indexes.Count}; physical stores={(Store == null ? 0 : 1)}; partitions={partitions}; reasons={reason}");
    // Configuration changes rebuild the shared tree, removing discarded namespaces.
    // Disabling every dimension drops its partitions; interrupted cleanup is retried.
    if (Store == null && Directory.Exists(directory))
    {
      var obsolete = Open();
      foreach (var partition in obsolete.ListPartitions()) obsolete.Drop(partition);
    }
    if (rebuild && indexes.Count > 0)
      Backfill(rawEvents);
    startup.Complete($"decision={(rebuild && indexes.Count > 0 ? "rebuild completed" : decision)}; indexes={indexes.Count}; physical stores={(Store == null ? 0 : 1)}");
  }

  PartitionedStore<Memory<byte>, Memory<byte>> Open()
  {
    var store = new PartitionedStore<Memory<byte>, Memory<byte>>(directory,
        options.RecordsPerPartition, factory.OpenDimensionIndex, logger,
        factory.CreateDimensionIndexMaintainer, factory.WriteAheadLogDirectoryOf);
    resources.Add(store);
    return store;
  }

  void Backfill(RawEventStore rawEvents)
  {
    using var build = new StartupLog(logger, "Dimension index backfill", directory);
    foreach (var partition in Store.ListPartitions()) Store.Drop(partition);
    var record = new RawEvent();
    var selector = new DimensionSelector(options.Dimensions);
    long records = 0;
    long rows = 0;
    long bytes = 0;
    long skipped = 0;
    var partitions = 0;
    using var writer = CreateWriter();
    foreach (var partition in rawEvents.Store.ListPartitions())
    {
      using var lease = rawEvents.Store.Acquire(partition);
      if (lease == null) continue;
      partitions++;
      using var iterator = lease.Tree.CreateIterator(new IteratorOptions { ContributeToTheBlockCache = true });
      iterator.SeekFirst();
      while (iterator.Next())
      {
        var key = iterator.CurrentKey;
        var value = iterator.CurrentValue;
        var rawKey = StorageKeyCodec.DecodeRawEvent(key.Span);
        if (!RawEventCodec.TryDecode(value.Span, rawKey.Ticks, null, default, record, selector)) { skipped++; continue; }
        var copies = writer.Write(rawKey.Site, rawKey.Ticks, rawKey.Sequence, record.Dimensions, value);
        rows += copies;
        bytes += (long)copies * value.Length;
        if (++records % 100_000 == 0) build.Progress($"events={records}; index rows written={rows}; payload bytes copied={bytes}", records, "events");
      }
    }
    writer.Dispose();
    Store.Compact();
    Store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    build.Complete($"indexes={indexes.Count}; physical stores=1; raw partitions={partitions}; events={records}; skipped events={skipped}; index rows written={rows}; payload bytes copied={bytes}", records, "events");
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
    if (Volatile.Read(ref invalid) != 0 || !factory.RawEventsDurable || !factory.DimensionIndexesDurable) return;
    meta.Update(entries =>
    {
      entries[DimensionsKey] = manifest;
      entries["dimension-index.format"] = Format;
      entries[PartitionsKey] = Store == null ? "" : string.Join(',', Store.ListPartitions());
      entries[CleanKey] = "true";
    });
  }

  public void Dispose() => Disposal.DisposeAll(resources);
}
