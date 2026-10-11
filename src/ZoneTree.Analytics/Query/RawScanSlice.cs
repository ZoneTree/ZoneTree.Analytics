using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>Decoder and session parts owned by one scan thread.</summary>
class RawScanSlice(int sessionShardCount)
{
  public readonly RawEvent RawEvent = new();

  public readonly Dictionary<ulong, RawSession>[] Sessions = Shards.Create<Dictionary<ulong, RawSession>>(sessionShardCount);
}
