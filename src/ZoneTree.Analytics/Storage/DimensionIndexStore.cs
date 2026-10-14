using System.Security.Cryptography;
using System.Text;
using ZoneTree.Analytics.Internal;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

sealed record DimensionIndex(string Dimension, string Id, PartitionedStore<Memory<byte>, Memory<byte>> Store);

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
    var rebuild = meta.Get(CleanKey) != "true" || meta.Get(DimensionsKey) != manifest || meta.Get("dimension-index.format") != "1";
    var registered = (meta.Get("dimension-index.registry") ?? meta.Get(DimensionsKey) ?? "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Union(options.Dimensions, StringComparer.Ordinal).ToArray();
    // Includes engines opened with no indexes: disabling and re-enabling an index must
    // never reuse copies that missed the events ingested while it was disabled.
    meta.Update(entries =>
    {
      entries[CleanKey] = "false";
      entries["dimension-index.registry"] = string.Join('\n', registered.Order(StringComparer.Ordinal));
    });
    // Removed indexes must not retain copies of private data. The registry is durable
    // before creation/deletion, so interrupted cleanup is retried on the next open.
    foreach (var dimension in registered.Except(options.Dimensions, StringComparer.Ordinal))
    {
      var obsolete = Open(dimension);
      foreach (var partition in obsolete.Store.ListPartitions()) obsolete.Store.Drop(partition);
    }
    foreach (var dimension in options.Dimensions)
    {
      var index = Open(dimension);
      indexes.Add(index);
      rebuild |= meta.Get(PartitionsKey(index.Id)) != string.Join(',', index.Store.ListPartitions());
    }
    if (rebuild && indexes.Count > 0)
    {
      foreach (var index in indexes)
        foreach (var partition in index.Store.ListPartitions()) index.Store.Drop(partition);
      Backfill(rawEvents);
    }
  }

  DimensionIndex Open(string dimension)
  {
    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dimension))).ToLowerInvariant();
    var store = new PartitionedStore<Memory<byte>, Memory<byte>>(Path.Combine(directory, id),
        options.RecordsPerPartition, factory.OpenDimensionIndex, logger,
        factory.CreateDimensionIndexMaintainer, factory.WriteAheadLogDirectoryOf);
    resources.Add(store);
    return new(dimension, id, store);
  }

  static string PartitionsKey(string id) => "dimension-index.partitions." + id;

  void Backfill(RawEventStore rawEvents)
  {
    logger.LogInfo($"Building {indexes.Count} covering dimension indexes from retained raw events.");
    var record = new RawEvent();
    var selector = new DimensionSelector(options.Dimensions);
    long records = 0;
    using var writer = CreateWriter();
    foreach (var partition in rawEvents.Store.ListPartitions())
    {
      using var lease = rawEvents.Store.Acquire(partition);
      if (lease == null) continue;
      using var iterator = lease.Tree.CreateIterator(new IteratorOptions { ContributeToTheBlockCache = true });
      iterator.SeekFirst();
      while (iterator.Next())
      {
        var key = iterator.CurrentKey;
        var value = iterator.CurrentValue;
        var rawKey = StorageKeyCodec.DecodeRawEvent(key.Span);
        if (!RawEventCodec.TryDecode(value.Span, rawKey.Ticks, null, default, record, selector)) continue;
        writer.Write(rawKey.Site, rawKey.Ticks, rawKey.Sequence, record.Dimensions, value);
        if (++records % 100_000 == 0) logger.LogInfo($"Dimension index build: {records:N0} retained events copied.");
      }
    }
    logger.LogInfo($"Dimension indexes ready: {records:N0} retained events copied.");
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

  static IEnumerable<Filter> Mandatory(IEnumerable<Filter> filters)
  {
    foreach (var filter in filters)
    {
      if (filter.Operator == FilterOperator.AllOf)
        foreach (var child in Mandatory(filter.Children)) yield return child;
      else yield return filter;
    }
  }

  static bool TryEquality(Filter filter, out string dimension, out string[] values)
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
      entries["dimension-index.registry"] = manifest;
      entries["dimension-index.format"] = "1";
      foreach (var index in indexes)
        entries[PartitionsKey(index.Id)] = string.Join(',', index.Store.ListPartitions());
      entries[CleanKey] = "true";
    });
  }

  public void Dispose() => Disposal.DisposeAll(resources);
}
