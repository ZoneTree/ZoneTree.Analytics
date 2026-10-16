using ZoneTree.Analytics.Ingestion;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// A detached flush whose rows and partition leases remain owned until all writes succeed.
/// Progress is retained across retries; newer events accumulate in a separate buffer.
/// </summary>
sealed class RollupWriteBatch(Dictionary<RollupBufferKey, RollupAccumulator> rows) : IDisposable
{
  public Dictionary<RollupBufferKey, RollupAccumulator> Rows = rows;

  public List<PartitionWrites> Partitions;

  public int NextPartition;

  public sealed class PartitionWrites(Granularity granularity, string name)
  {
    public readonly Granularity Granularity = granularity;

    public readonly string Name = name;

    public readonly List<RollupWrite> Rows = [];

    public int NextRow;

    // Keep a failed write on the same tree, including its in-memory value and writer state.
    public PartitionLease<Memory<byte>, RollupValue> Lease;
  }

  public void Dispose()
  {
    if (Partitions == null)
      return;
    foreach (var partition in Partitions)
    {
      partition.Lease?.Dispose();
      partition.Lease = null;
    }
  }
}

/// <summary>How a write changes its row.</summary>
enum RollupWriteKind
{
  /// <summary>Merges an additive delta of ingestion into the row.</summary>
  Delta,

  /// <summary>Replaces the rebuilt part of the row with the absolute value of a reconstruction.</summary>
  Rebuild,

  /// <summary>Merges the rebuilt part into the row, and copies the row to its indexes.</summary>
  Fold
}

/// <summary>One change of a row and whether its replacement has been written.</summary>
/// <param name="key">The rollup key.</param>
/// <param name="delta">The delta, or the rebuilt part a <see cref="RollupWriteKind.Rebuild"/> writes; null for a fold.</param>
/// <param name="kind">How the write changes the row.</param>
sealed class RollupWrite(Memory<byte> key, RollupValue delta, RollupWriteKind kind = RollupWriteKind.Delta)
{
  public readonly Memory<byte> Key = key;

  public readonly RollupValue Delta = delta;

  public readonly RollupWriteKind Kind = kind;

  public RollupValue Replacement;

  // A fold of a row without a rebuilt part only copies the row.
  public bool Unchanged;

  public bool Completed;
}

/// <summary>
/// Serializes the updates of each rollup row of one partition. A row's key selects one of a
/// fixed set of lock stripes, so workers flushing into the same partition update different rows
/// in parallel and the same row one at a time. A failed upsert may already have changed the
/// tree's in-memory value: its stripe retries the absolute replacement before applying any
/// further delta. This also lets another worker finish the pending write without applying it
/// twice.
/// </summary>
/// <remarks>
/// Background index builds and history reconstructions write through the same stripes, so they
/// read the current value of a row and never overwrite a concurrent flush. Only ingestion records
/// the days it writes for index recovery: background writes advance their own checkpoints.
/// </remarks>
sealed class RollupWriter(IZoneTree<Memory<byte>, RollupValue> tree, Action<Memory<byte>, RollupValue> copy = null,
    Action<Memory<byte>> beforeWrite = null)
{
  /// <summary>
  /// The stripes of a partition. Many more than the workers, so that workers flushing distinct
  /// rows rarely wait for each other.
  /// </summary>
  const int StripeCount = 64;

  /// <summary>A lock and the write it has not completed yet, if an upsert failed.</summary>
  sealed class Stripe
  {
    public RollupWrite Pending;
  }

  readonly Stripe[] stripes = CreateStripes();

  static Stripe[] CreateStripes()
  {
    var stripes = new Stripe[StripeCount];
    for (var i = 0; i < stripes.Length; ++i)
      stripes[i] = new Stripe();
    return stripes;
  }

  /// <summary>The stripe of a row: the same key always has the same stripe.</summary>
  internal static int StripeOf(ReadOnlySpan<byte> key)
  {
    var hash = new HashCode();
    hash.AddBytes(key);
    return (int)((uint)hash.ToHashCode() % StripeCount);
  }

  public void Write(RollupWrite row)
  {
    var stripe = stripes[StripeOf(row.Key.Span)];
    lock (stripe)
    {
      CompletePending(stripe);
      if (row.Completed)
        return;
      tree.TryGet(row.Key, out var existing);
      row.Replacement = row.Kind switch
      {
        RollupWriteKind.Delta => RollupValue.Merge(existing, row.Delta),
        RollupWriteKind.Rebuild => RollupValue.WithRebuilt(existing, row.Delta),
        _ => existing?.Folded()
      };
      if (row.Replacement == null)
      {
        // A fold of a row that no longer exists.
        row.Completed = true;
        return;
      }
      row.Unchanged = ReferenceEquals(row.Replacement, existing);
      stripe.Pending = row;
      CompletePending(stripe);
    }
  }

  void CompletePending(Stripe stripe)
  {
    var pending = stripe.Pending;
    if (pending == null)
      return;
    // Every writer of this row uses the same stripe. An upsert retry cannot overwrite another
    // worker's contribution, and repeating it cannot add the delta a second time. The index
    // copy runs under the same lock, so the copies of a row are written in order. A rebuilt part
    // is invisible to readers: it reaches the indexes when it is folded in.
    if (pending.Kind == RollupWriteKind.Delta)
      beforeWrite?.Invoke(pending.Key);
    if (!pending.Unchanged)
      tree.Upsert(pending.Key, pending.Replacement);
    if (pending.Kind != RollupWriteKind.Rebuild)
      copy?.Invoke(pending.Key, pending.Replacement);
    pending.Completed = true;
    stripe.Pending = null;
  }
}
