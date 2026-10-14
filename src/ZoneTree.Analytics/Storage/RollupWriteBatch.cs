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

/// <summary>One additive contribution and whether its replacement has been written.</summary>
sealed class RollupWrite(Memory<byte> key, RollupValue delta)
{
  public readonly Memory<byte> Key = key;

  public readonly RollupValue Delta = delta;

  public RollupValue Replacement;

  public bool Completed;
}

/// <summary>
/// Serializes rollup updates of one partition. A failed upsert may already have changed the
/// tree's in-memory value: retry its absolute replacement before applying any further delta.
/// This also lets another worker finish the pending write without applying it twice.
/// </summary>
sealed class RollupWriter(IZoneTree<Memory<byte>, RollupValue> tree, Action<Memory<byte>, RollupValue> copy = null)
{
  readonly object sync = new();

  RollupWrite pending;

  public void Write(RollupWrite row)
  {
    lock (sync)
    {
      CompletePending();
      if (row.Completed)
        return;
      tree.TryGet(row.Key, out var existing);
      row.Replacement = RollupValue.Merge(existing, row.Delta);
      pending = row;
      CompletePending();
    }
  }

  void CompletePending()
  {
    if (pending == null)
      return;
    // Every rollup writer for this tree uses the same lock. An upsert retry cannot overwrite
    // another worker's contribution, and repeating it cannot add the delta a second time.
    tree.Upsert(pending.Key, pending.Replacement);
    copy?.Invoke(pending.Key, pending.Replacement);
    pending.Completed = true;
    pending = null;
  }
}
