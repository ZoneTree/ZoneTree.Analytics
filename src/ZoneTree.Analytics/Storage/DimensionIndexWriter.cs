namespace ZoneTree.Analytics.Storage;

/// <summary>One shared partition lease per worker avoids registry locks for every index copy.</summary>
sealed class DimensionIndexWriter(DimensionIndexStore owner) : IDisposable
{
  PartitionLease<Memory<byte>, Memory<byte>> lease;

  public int Write(string site, long ticks, long sequence,
      IEnumerable<KeyValuePair<string, string>> dimensions, Memory<byte> value)
  {
    if (!owner.IsHealthy) return 0;
    var written = 0;
    IZoneTree<Memory<byte>, Memory<byte>> tree = null;
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
        tree ??= Tree(ticks);
        if (tree == null) break;
        tree.Upsert(DimensionIndexKeyCodec.Key(index.KeyPrefix, site, match, ticks, sequence), value);
        written++;
      }
    }
    catch { owner.Invalidate(); throw; }
    return written;
  }

  public void Delete(RawEvent record)
  {
    if (owner.Indexes.Count == 0) return;
    try
    {
      var tree = Tree(record.Ticks);
      if (tree == null) return;
      for (var i = 0; i < owner.Indexes.Count; ++i)
        if (record.TryGetDimension(owner.Indexes[i].Dimension, out var value) && value != null)
          tree.ForceDelete(DimensionIndexKeyCodec.Key(owner.Indexes[i].KeyPrefix, record.Site, value, record.Ticks, record.Sequence));
    }
    catch { owner.Invalidate(); throw; }
  }

  IZoneTree<Memory<byte>, Memory<byte>> Tree(long ticks)
  {
    var store = owner.Store;
    var name = store.Route(ticks);
    if (lease?.Name != name)
    {
      lease?.Dispose();
      lease = null;
      lease = store.Acquire(name);
    }
    return lease?.Tree;
  }

  public void Dispose()
  {
    lease?.Dispose();
    lease = null;
  }
}
