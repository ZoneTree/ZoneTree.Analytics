using ZoneTree.Analytics.Internal;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Time-partitioned raw event storage. Keys are ordered by site, then time,
/// so a site's time range is one contiguous scan per partition.
/// </summary>
sealed class RawEventStore : IDisposable
{
  /// <summary>
  /// Scans check for cancellation once per this many events.
  /// </summary>
  public const int CancellationCheckInterval = 1024;

  /// <summary>
  /// Parallel scans split their range into about this many slices per thread, so that threads
  /// that finish early take over the remaining slices when traffic is uneven over time.
  /// </summary>
  const int SlicesPerThread = 4;

  /// <summary>
  /// Slices are not made shorter than this; each slice costs an iterator and a seek.
  /// </summary>
  const long MinSliceTicks = TimeSpan.TicksPerMinute;

  public RawEventStore(
      string rootDirectory, long recordsPerPartition, StoreFactory factory, ILogger logger)
  {
    Store = new(rootDirectory, recordsPerPartition, factory.OpenRawEvents, logger, factory.CreateRawEventsMaintainer, factory.WriteAheadLogDirectoryOf);
  }

  public PartitionedStore<Memory<byte>, Memory<byte>> Store { get; }

  /// <summary>
  /// Receives one event of a slice: its timestamp and encoded value.
  /// </summary>
  public delegate void SliceVisitor<in TState>(TState state, long ticks, ReadOnlySpan<byte> value);

  readonly record struct Slice(IZoneTree<Memory<byte>, Memory<byte>> Tree, long From, long To);

  /// <summary>
  /// Scans the events of a site with timestamps in [fromTicks, toTicks).
  /// </summary>
  /// <remarks>
  /// The range is split into time slices, within partitions, that are scanned on up to
  /// <paramref name="maxDegreeOfParallelism"/> threads. Each slice gets its own state from
  /// <paramref name="createState"/> and visits its events in time order. The states are returned
  /// in time order, so callers can merge per-slice results that depend on event order.
  /// </remarks>
  public TState[] Scan<TState>(
      string site,
      long fromTicks,
      long toTicks,
      int maxDegreeOfParallelism,
      Func<TState> createState,
      SliceVisitor<TState> visit,
      CancellationToken cancellationToken)
  {
    if (fromTicks >= toTicks)
      return [];
    var leases = new List<PartitionLease<Memory<byte>, Memory<byte>>>();
    try
    {
      var ranges = new List<Slice>();
      foreach (var partition in Store.PartitionsInRange(fromTicks, toTicks))
      {
        var lease = Store.Acquire(partition.Name);
        if (lease == null)
          continue;
        leases.Add(lease);
        ranges.Add(new Slice(lease.Tree, Math.Max(fromTicks, partition.From), Math.Min(toTicks, partition.To)));
      }
      var slices = maxDegreeOfParallelism == 1 ? ranges : Split(ranges, maxDegreeOfParallelism * SlicesPerThread);
      var sitePrefix = KeyCodec.SitePrefix(site);
      var states = new TState[slices.Count];
      ParallelWork.For(slices.Count, maxDegreeOfParallelism, i =>
      {
        cancellationToken.ThrowIfCancellationRequested();
        var state = createState();
        ScanSlice(slices[i], site, sitePrefix, state, visit, cancellationToken);
        states[i] = state;
      });
      return states;
    }
    finally
    {
      foreach (var lease in leases)
        lease.Dispose();
    }
  }

  /// <summary>
  /// Splits partition ranges into about <paramref name="targetCount"/> slices of similar duration.
  /// </summary>
  static List<Slice> Split(List<Slice> ranges, int targetCount)
  {
    long total = 0;
    foreach (var range in ranges)
      total += range.To - range.From;
    var slices = new List<Slice>(targetCount + ranges.Count);
    foreach (var range in ranges)
    {
      var length = range.To - range.From;
      var count = (int)Math.Clamp(
          Math.Round((double)targetCount * length / total), 1, Math.Max(1, length / MinSliceTicks));
      var from = range.From;
      for (var i = 1; i <= count; ++i)
      {
        var to = i == count ? range.To : range.From + (long)((Int128)length * i / count);
        slices.Add(range with { From = from, To = to });
        from = to;
      }
    }
    return slices;
  }

  static void ScanSlice<TState>(
      Slice slice, string site, byte[] sitePrefix, TState state, SliceVisitor<TState> visit, CancellationToken cancellationToken)
  {
    // Slices of one scan, and repeated scans, share decompressed blocks and their materialized
    // entries through the block cache of the partition; the maintainer evicts unused blocks.
    using var iterator = slice.Tree.CreateIterator(new IteratorOptions
    {
      DiskSegmentPrefetchSize = 64,
      ContributeToTheBlockCache = true
    });
    iterator.Seek(KeyCodec.RawKey(site, slice.From, 0));
    var counter = 0;
    while (iterator.Next())
    {
      if (++counter % CancellationCheckInterval == 0)
        cancellationToken.ThrowIfCancellationRequested();
      var key = iterator.CurrentKey.Span;
      if (key.Length != sitePrefix.Length + 16 || !key.StartsWith(sitePrefix))
        break;
      var ticks = KeyCodec.ReadRawTicks(key, sitePrefix.Length);
      if (ticks >= slice.To)
        break;
      visit(state, ticks, iterator.CurrentValue.Span);
    }
  }

  public void Dispose() => Store.Dispose();
}
