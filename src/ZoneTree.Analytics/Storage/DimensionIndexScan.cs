using ZoneTree.Analytics.Internal;
using Slice = ZoneTree.Analytics.Storage.RawEventStore.Slice;

namespace ZoneTree.Analytics.Storage;

/// <summary>Ordered covering reads, including k-way merging of distinct equality values.</summary>
static class DimensionIndexScan
{
  sealed class Cursor : IDisposable
  {
    readonly IZoneTreeIterator<Memory<byte>, Memory<byte>> iterator;
    readonly byte[] prefix;
    readonly long to;
    public long Ticks { get; private set; }
    public long Sequence { get; private set; }
    public Memory<byte> Value => iterator.CurrentValue;

    public Cursor(IZoneTree<Memory<byte>, Memory<byte>> tree, byte[] prefix, long from, long to)
    {
      this.prefix = prefix;
      this.to = to;
      iterator = tree.CreateIterator(new IteratorOptions { DiskSegmentPrefetchSize = 64, ContributeToTheBlockCache = true });
      try { iterator.Seek(KeyCodec.SeekKey(prefix, from)); }
      catch { iterator.Dispose(); throw; }
    }

    public bool Next()
    {
      if (!iterator.Next()) return false;
      var key = iterator.CurrentKey.Span;
      if (key.Length != prefix.Length + 16 || !key.StartsWith(prefix)) return false;
      Ticks = KeyCodec.ReadRawTicks(key);
      Sequence = DimensionIndexKeyCodec.Sequence(key);
      return Ticks < to;
    }

    public void Dispose() => iterator.Dispose();
  }

  public static int Count(string site, long from, long to, DimensionIndexSelection selection, int limit, CancellationToken cancellationToken)
  {
    var count = 0;
    foreach (var partition in selection.Index.Store.PartitionsInRange(from, to))
    {
      cancellationToken.ThrowIfCancellationRequested();
      using var lease = selection.Index.Store.Acquire(partition.Name);
      if (lease == null) continue;
      foreach (var value in selection.Values)
      {
        using var cursor = new Cursor(lease.Tree, DimensionIndexKeyCodec.Prefix(selection.Index.KeyPrefix, site, value), Math.Max(from, partition.From), Math.Min(to, partition.To));
        while (cursor.Next())
        {
          if (++count >= limit) return count;
          if (count % RawEventStore.CancellationCheckInterval == 0) cancellationToken.ThrowIfCancellationRequested();
        }
      }
    }
    return count;
  }

  public static TState[] Scan<TState>(DimensionIndexStore owner, DimensionIndexSelection selection,
      string site, long from, long to, int parallelism, Func<TState> createState,
      RawEventStore.SliceVisitor<TState> visit, CancellationToken cancellationToken)
  {
    owner.Check(selection);
    var leases = new List<PartitionLease<Memory<byte>, Memory<byte>>>();
    try
    {
      var ranges = new List<Slice>();
      foreach (var partition in selection.Index.Store.PartitionsInRange(from, to))
      {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = selection.Index.Store.Acquire(partition.Name);
        if (lease == null) continue;
        leases.Add(lease);
        ranges.Add(new(lease.Tree, Math.Max(from, partition.From), Math.Min(to, partition.To)));
      }
      var slices = parallelism == 1 ? ranges : RawEventStore.Split(ranges, parallelism * 4);
      var prefixes = selection.Values.Select(value => DimensionIndexKeyCodec.Prefix(selection.Index.KeyPrefix, site, value)).ToArray();
      var states = new TState[slices.Count];
      ParallelWork.For(slices.Count, parallelism, i =>
      {
        cancellationToken.ThrowIfCancellationRequested();
        var state = createState();
        Read(slices[i], prefixes, state, visit, cancellationToken);
        states[i] = state;
      });
      owner.Check(selection);
      return states;
    }
    finally { foreach (var lease in leases) lease.Dispose(); }
  }

  static void Read<TState>(Slice slice, byte[][] prefixes, TState state,
      RawEventStore.SliceVisitor<TState> visit, CancellationToken cancellationToken)
  {
    var cursors = new List<Cursor>();
    try
    {
      var queue = new PriorityQueue<Cursor, (long Ticks, long Sequence)>();
      foreach (var prefix in prefixes)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = new Cursor(slice.Tree, prefix, slice.From, slice.To);
        cursors.Add(cursor);
        if (cursor.Next()) queue.Enqueue(cursor, (cursor.Ticks, cursor.Sequence));
      }
      var count = 0;
      while (queue.TryDequeue(out var cursor, out _))
      {
        if (++count % RawEventStore.CancellationCheckInterval == 0) cancellationToken.ThrowIfCancellationRequested();
        visit(state, cursor.Ticks, cursor.Value.Span);
        if (cursor.Next()) queue.Enqueue(cursor, (cursor.Ticks, cursor.Sequence));
      }
    }
    finally { foreach (var cursor in cursors) cursor.Dispose(); }
  }
}
