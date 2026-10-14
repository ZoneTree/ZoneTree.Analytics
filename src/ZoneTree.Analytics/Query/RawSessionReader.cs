using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>The reconstructed events and page flow of one raw session.</summary>
sealed class RawSession
{
  public long Start;

  public long Last;

  public int PageViews;

  public int Interactions;

  public ulong Visitor;

  public bool Allowed;

  public Dictionary<string, string> Dimensions;

  public bool HasStartEvent;

  public bool FirstIsPageView;

  public string FirstPage;

  public string FirstHostname;

  public string LastPage;

  public string LastHostname;

  public long LastPageTicks;

  /// <summary>
  /// Appends the part of the session seen in a later slice. Every event of that part is later
  /// than the events seen so far. A later slice may contain the actual start event after an
  /// earlier late event initialized reconstruction; otherwise the entrance and dimensions
  /// stay. The last page view of the part, if it has one, replaces the current one.
  /// </summary>
  public void Append(RawSession later)
  {
    Last = Math.Max(Last, later.Last);
    PageViews += later.PageViews;
    Interactions += later.Interactions;
    if (!HasStartEvent && later.HasStartEvent)
    {
      HasStartEvent = true;
      FirstIsPageView = later.FirstIsPageView;
      FirstPage = later.FirstPage;
      FirstHostname = later.FirstHostname;
    }
    if (later.LastPage != null)
    {
      LastPage = later.LastPage;
      LastHostname = later.LastHostname;
      LastPageTicks = later.LastPageTicks;
    }
  }
}

/// <summary>
/// Reconstructs sessions independently of metric aggregation. Slice dictionaries are owned by
/// their scan threads; joined sessions are only updated after each scan has completed.
/// </summary>
sealed class RawSessionReader(RawEventStore events, SessionSchema schema, AnalyticsOptions options)
{
  readonly int parallelism = options.Queries.MaxDegreeOfParallelism;

  readonly long timeout = options.Sessions.Timeout.Ticks;

  readonly long futureSkew = options.Ingestion.MaxFutureSkew.Ticks;

  /// <summary>Joins slices in time order, consuming their session dictionaries.</summary>
  public Dictionary<ulong, RawSession>[] Join(RawScanSlice[] slices)
  {
    var sessions = new Dictionary<ulong, RawSession>[parallelism];
    ParallelWork.For(parallelism, parallelism, shard => sessions[shard] = JoinShard(slices, shard));
    return sessions;
  }

  /// <summary>
  /// Completes sessions starting in the requested range with earlier assigned late events.
  /// Ingestion admits timestamps down to Start - Timeout, so the extra scan is bounded.
  /// Only already discovered session IDs are collected; ordinary event counts stay in range.
  /// Returns the earliest time read, or null when no lookback is needed.
  /// </summary>
  public long? ReadPreceding(
      string site, long from, long to, DimensionSelector selector,
      Dictionary<ulong, RawSession>[] sessions, CancellationToken cancellationToken, DimensionIndexSelection index = null)
  {
    var earliest = from;
    foreach (var shard in sessions)
    {
      foreach (var session in shard.Values)
      {
        if (session.Start >= from && session.Start < to)
          earliest = Math.Min(earliest, session.Start - Math.Min(timeout, session.Start));
      }
    }
    if (earliest == from)
      return null;

    var slices = events.ScanIndexed(index, site, earliest, from, parallelism, () => new RawScanSlice(parallelism),
        (slice, ticks, value) =>
        {
          var id = RawEventCodec.ReadSessionId(value);
          if (id == 0)
            return;
          var shard = Shards.Of(id, parallelism);
          if (!sessions[shard].TryGetValue(id, out var session) || session.Start < from || session.Start >= to)
            return;
          if (RawEventCodec.TryDecode(value, ticks, null, default, slice.RawEvent, selector))
            Collect(slice.Sessions[shard], slice.RawEvent, session.Allowed);
        }, cancellationToken, out _);
    var preceding = Join(slices);
    ParallelWork.For(parallelism, parallelism, shard =>
    {
      foreach (var (id, part) in preceding[shard])
      {
        part.Append(sessions[shard][id]);
        sessions[shard][id] = part;
      }
    });
    return earliest;
  }

  /// <summary>
  /// Completes only sessions found in the requested range. A timeout limits inactivity,
  /// not total duration: each scan reaches the latest active session's next deadline.
  /// </summary>
  public void ReadContinuation(
      string site, long from, long now, DimensionSelector selector,
      Dictionary<ulong, RawSession>[] sessions, CancellationToken cancellationToken, DimensionIndexSelection index = null)
  {
    var latest = now + Math.Min(futureSkew, DateTime.MaxValue.Ticks - now);
    while (from < latest)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var activeAfter = from - timeout;
      var last = LatestActiveEvent(sessions, activeAfter);
      if (last == long.MinValue)
        return;
      var to = Math.Min(latest, last + Math.Min(timeout, DateTime.MaxValue.Ticks - last) + 1);
      if (to <= from)
        return;

      var slices = events.ScanIndexed(index, site, from, to, parallelism, () => new RawScanSlice(parallelism),
          (slice, ticks, value) =>
          {
            var sessionId = RawEventCodec.ReadSessionId(value);
            if (sessionId == 0)
              return;
            var shard = Shards.Of(sessionId, parallelism);
            if (!sessions[shard].TryGetValue(sessionId, out var session) || session.Last < activeAfter)
              return;
            var rawEvent = slice.RawEvent;
            if (RawEventCodec.TryDecode(value, ticks, null, default, rawEvent, selector))
              Collect(slice.Sessions[shard], rawEvent, session.Allowed);
          }, cancellationToken, out _);
      var continuation = Join(slices);
      ParallelWork.For(parallelism, parallelism, shard =>
      {
        foreach (var (id, part) in continuation[shard])
          sessions[shard][id].Append(part);
      });
      from = to;
    }
  }

  static long LatestActiveEvent(Dictionary<ulong, RawSession>[] sessions, long activeAfter)
  {
    var last = long.MinValue;
    foreach (var shard in sessions)
    {
      foreach (var session in shard.Values)
      {
        if (session.Last >= activeAfter)
          last = Math.Max(last, session.Last);
      }
    }
    return last;
  }

  public void Collect(Dictionary<ulong, RawSession> sessions, RawEvent rawEvent, bool allowed)
  {
    if (!sessions.TryGetValue(rawEvent.SessionId, out var session))
    {
      session = new RawSession
      {
        Start = rawEvent.SessionStartTicks,
        Last = rawEvent.Ticks,
        Visitor = rawEvent.Visitor,
        Allowed = allowed
      };
      sessions.Add(rawEvent.SessionId, session);
    }
    // An earlier late event can initialize the reconstructed session before its start
    // event is scanned. At equal timestamps, scan order preserves ingestion order.
    var isFirstEvent = rawEvent.Ticks == session.Start && !session.HasStartEvent;
    if (isFirstEvent)
      session.HasStartEvent = true;
    if (session.Dimensions == null)
    {
      var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var (key, value) in rawEvent.Dimensions)
      {
        if (key == StandardDimensions.EntryPage || schema.IsSessionLevel(key))
          dimensions[key] = value;
      }
      session.Dimensions = dimensions;
    }
    if (rawEvent.Ticks > session.Last)
      session.Last = rawEvent.Ticks;
    if (rawEvent.Interactive)
      ++session.Interactions;
    if (rawEvent.Name != StandardEvents.PageView)
      return;
    ++session.PageViews;
    if (!rawEvent.TryGetDimension(StandardDimensions.Page, out var page))
      return;
    rawEvent.TryGetDimension(StandardDimensions.Hostname, out var hostname);
    if (isFirstEvent)
    {
      session.FirstIsPageView = true;
      session.FirstPage = page;
      session.FirstHostname = hostname ?? "";
    }
    if (session.LastPage == null || rawEvent.Ticks >= session.LastPageTicks)
    {
      session.LastPage = page;
      session.LastHostname = hostname ?? "";
      session.LastPageTicks = rawEvent.Ticks;
    }
  }

  static Dictionary<ulong, RawSession> JoinShard(RawScanSlice[] slices, int shard)
  {
    if (slices.Length == 0)
      return [];
    var joined = slices[0].Sessions[shard];
    for (var i = 1; i < slices.Length; ++i)
    {
      foreach (var (id, part) in slices[i].Sessions[shard])
      {
        if (joined.TryGetValue(id, out var session))
          session.Append(part);
        else
          joined.Add(id, part);
      }
    }
    return joined;
  }
}
