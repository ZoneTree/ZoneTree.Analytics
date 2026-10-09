using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>
/// The visitors and sessions selected by behavioral segments, collected in a raw event pre-pass
/// shared by queries and funnels.
/// </summary>
sealed class SegmentSets
{
  readonly Segment[] segments;

  // Members by segment, then by shard of the visitor or session id.
  readonly HashSet<ulong>[][] members;

  SegmentSets(Segment[] segments, HashSet<ulong>[][] members)
  {
    this.segments = segments;
    this.members = members;
  }

  sealed class SliceState(int segmentCount, int shardCount)
  {
    public readonly RawEvent RawEvent = new();

    public readonly HashSet<ulong>[][] Members =
        Enumerable.Range(0, segmentCount).Select(_ => Shards.Create<HashSet<ulong>>(shardCount)).ToArray();
  }

  /// <summary>
  /// Collects the visitors and sessions with a matching event in [from, to).
  /// </summary>
  public static SegmentSets Evaluate(
      RawEventStore rawEvents,
      string site,
      long from,
      long to,
      Segment[] segments,
      int maxDegreeOfParallelism,
      CancellationToken cancellationToken)
  {
    var shardCount = maxDegreeOfParallelism;
    var selector = new DimensionSelector(segments.SelectMany(x => x.Filters).Select(x => x.Dimension));
    var slices = rawEvents.Scan(
        site, from, to, maxDegreeOfParallelism, () => new SliceState(segments.Length, shardCount),
        (state, ticks, value) =>
        {
          var rawEvent = state.RawEvent;
          if (!RawEventCodec.TryDecode(value, ticks, null, default, rawEvent, selector))
            return;
          for (var i = 0; i < segments.Length; ++i)
          {
            var id = IdOf(segments[i], rawEvent.Visitor, rawEvent.SessionId);
            if (id != 0 && segments[i].Matches(rawEvent))
              state.Members[i][Shards.Of(id, shardCount)].Add(id);
          }
        },
        cancellationToken);

    // Sets are unions, so slices merge in any order; each segment and shard merges on its own.
    var members = Enumerable.Range(0, segments.Length).Select(_ => new HashSet<ulong>[shardCount]).ToArray();
    ParallelWork.For(segments.Length * shardCount, maxDegreeOfParallelism, index =>
    {
      var (segment, shard) = Math.DivRem(index, shardCount);
      HashSet<ulong> merged = null;
      foreach (var slice in slices)
      {
        var set = slice.Members[segment][shard];
        if (merged == null || set.Count > merged.Count)
          (merged, set) = (set, merged);
        if (set != null)
          merged.UnionWith(set);
      }
      members[segment][shard] = merged ?? [];
    });
    return new SegmentSets(segments, members);
  }

  static ulong IdOf(Segment segment, ulong visitor, ulong session) =>
      segment.Scope == SegmentScope.Visitors ? visitor : session;

  /// <summary>
  /// True when the visitor and session satisfy every segment.
  /// </summary>
  public bool Allows(ulong visitor, ulong session)
  {
    for (var i = 0; i < segments.Length; ++i)
    {
      var id = IdOf(segments[i], visitor, session);
      var shards = members[i];
      var isMember = id != 0 && shards[Shards.Of(id, shards.Length)].Contains(id);
      if (isMember == segments[i].IsExcluded)
        return false;
    }
    return true;
  }
}
