using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>
/// The visitors and sessions selected by behavioral segments, collected in a raw event pre-pass
/// shared by queries and funnels.
/// </summary>
sealed class SegmentSets
{
  /// <summary>
  /// Raw scans check for cancellation once per this many events.
  /// </summary>
  public const int CancellationCheckInterval = 1024;

  readonly Segment[] segments;

  readonly HashSet<ulong>[] members;

  SegmentSets(Segment[] segments)
  {
    this.segments = segments;
    members = segments.Select(_ => new HashSet<ulong>()).ToArray();
  }

  /// <summary>
  /// Collects the visitors and sessions with a matching event in [from, to).
  /// </summary>
  public static SegmentSets Evaluate(
      RawEventStore rawEvents, string site, long from, long to, Segment[] segments, CancellationToken cancellationToken)
  {
    var sets = new SegmentSets(segments);
    var selector = new DimensionSelector(segments.SelectMany(x => x.Filters).Select(x => x.Dimension));
    var rawEvent = new RawEvent();
    var counter = 0;
    string Lookup(string dimension) => rawEvent.TryGetDimension(dimension, out var value) ? value : null;
    rawEvents.Scan(site, from, to, (ticks, _, value) =>
    {
      if (++counter % CancellationCheckInterval == 0)
        cancellationToken.ThrowIfCancellationRequested();
      if (!RawEventCodec.TryDecode(value.Span, ticks, null, default, rawEvent, selector))
        return true;
      for (var i = 0; i < segments.Length; ++i)
      {
        var id = sets.IdOf(i, rawEvent.Visitor, rawEvent.SessionId);
        if (id != 0 && segments[i].Matches(rawEvent.Name, Lookup))
          sets.members[i].Add(id);
      }
      return true;
    });
    return sets;
  }

  ulong IdOf(int segment, ulong visitor, ulong session) =>
      segments[segment].Scope == SegmentScope.Visitors ? visitor : session;

  /// <summary>
  /// True when the visitor and session satisfy every segment.
  /// </summary>
  public bool Allows(ulong visitor, ulong session)
  {
    for (var i = 0; i < segments.Length; ++i)
    {
      var id = IdOf(i, visitor, session);
      var isMember = id != 0 && members[i].Contains(id);
      if (isMember == segments[i].IsExcluded)
        return false;
    }
    return true;
  }
}
