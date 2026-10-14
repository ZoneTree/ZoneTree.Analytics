namespace ZoneTree.Analytics.Storage;

/// <summary>Per-worker leases avoid acquiring partition locks for each indexed event.</summary>
sealed class DimensionIndexWriter(DimensionIndexStore owner) : IDisposable
{
  readonly PartitionLease<Memory<byte>, Memory<byte>>[] leases = new PartitionLease<Memory<byte>, Memory<byte>>[owner.Indexes.Count];

  public int Write(string site, long ticks, long sequence,
      IEnumerable<KeyValuePair<string, string>> dimensions, Memory<byte> value)
  {
    if (!owner.IsHealthy) return 0;
    var written = 0;
    try
    {
      for (var i = 0; i < owner.Indexes.Count; ++i)
      {
        var index = owner.Indexes[i];
        string match = null;
        if (dimensions is IReadOnlyDictionary<string, string> dictionary)
          dictionary.TryGetValue(index.Dimension, out match);
        else
          foreach (var dimension in dimensions)
            if (dimension.Key == index.Dimension) { match = dimension.Value; break; }
        if (match == null) continue;
        var tree = Tree(i, ticks);
        if (tree == null) continue;
        tree.Upsert(DimensionIndexKeyCodec.Key(site, match, ticks, sequence), value);
        written++;
      }
    }
    catch { owner.Invalidate(); throw; }
    return written;
  }

  public void Delete(RawEvent record)
  {
    try
    {
      for (var i = 0; i < owner.Indexes.Count; ++i)
        if (record.TryGetDimension(owner.Indexes[i].Dimension, out var value) && value != null)
          Tree(i, record.Ticks)?.ForceDelete(DimensionIndexKeyCodec.Key(record.Site, value, record.Ticks, record.Sequence));
    }
    catch { owner.Invalidate(); throw; }
  }

  IZoneTree<Memory<byte>, Memory<byte>> Tree(int index, long ticks)
  {
    var store = owner.Indexes[index].Store;
    var name = store.Route(ticks);
    if (leases[index]?.Name != name)
    {
      leases[index]?.Dispose();
      leases[index] = null;
      leases[index] = store.Acquire(name);
    }
    return leases[index]?.Tree;
  }

  public void Dispose()
  {
    for (var i = 0; i < leases.Length; ++i)
    {
      leases[i]?.Dispose();
      leases[i] = null;
    }
  }
}
