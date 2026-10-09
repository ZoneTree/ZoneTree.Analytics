using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Time-partitioned raw event storage. Keys are ordered by site, then time,
/// so a site's time range is one contiguous scan per partition.
/// </summary>
sealed class RawEventStore : IDisposable
{
  public RawEventStore(
      string rootDirectory, PartitionPeriod period, StoreFactory factory, ILogger logger)
  {
    Store = new(rootDirectory, new PartitionScheme(period), factory.OpenRawEvents, logger, factory.CreateRawEventsMaintainer, factory.WriteAheadLogDirectoryOf);
  }

  public PartitionedStore<Memory<byte>, Memory<byte>> Store { get; }

  public delegate bool RawEventVisitor(long ticks, Memory<byte> key, Memory<byte> value);

  /// <summary>
  /// Scans the events of a site with timestamps in [fromTicks, toTicks).
  /// The visitor returns false to stop.
  /// </summary>
  public void Scan(string site, long fromTicks, long toTicks, RawEventVisitor visit)
  {
    if (fromTicks >= toTicks)
      return;
    var sitePrefix = KeyCodec.SitePrefix(site);
    var seekKey = KeyCodec.RawKey(site, fromTicks, 0);
    foreach (var partition in Store.Scheme.NamesInRange(fromTicks, toTicks))
    {
      using var lease = Store.Acquire(partition, create: false);
      if (lease == null)
        continue;
      // A raw scan reads each block once; it stays out of the block cache to keep hot rollup blocks.
      using var iterator = lease.Tree.CreateIterator(new IteratorOptions
      {
        DiskSegmentPrefetchSize = 64
      });
      iterator.Seek(seekKey);
      while (iterator.Next())
      {
        var key = iterator.CurrentKey;
        var span = key.Span;
        if (span.Length != sitePrefix.Length + 16 || !span.StartsWith(sitePrefix))
          break;
        var ticks = KeyCodec.ReadRawTicks(span, sitePrefix.Length);
        if (ticks >= toTicks)
          break;
        if (!visit(ticks, key, iterator.CurrentValue))
          return;
      }
    }
  }

  public void Dispose() => Store.Dispose();
}
