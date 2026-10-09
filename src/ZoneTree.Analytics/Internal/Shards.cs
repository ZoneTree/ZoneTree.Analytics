namespace ZoneTree.Analytics.Internal;

/// <summary>
/// Spreads 64-bit ids, such as visitor and session hashes, over shards. Parallel scans keep
/// per-slice data keyed by id in shards, so the slices can be merged shard by shard in parallel.
/// </summary>
static class Shards
{
  public static int Of(ulong id, int count) =>
      (int)((((id * 0x9E3779B97F4A7C15UL) >> 32) * (uint)count) >> 32);

  /// <summary>
  /// Creates one empty container per shard.
  /// </summary>
  public static T[] Create<T>(int count) where T : new()
  {
    var shards = new T[count];
    for (var i = 0; i < count; ++i)
      shards[i] = new T();
    return shards;
  }
}
