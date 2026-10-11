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

  public bool FirstIsPageView;

  public string FirstPage;

  public string FirstHostname;

  public string LastPage;

  public string LastHostname;

  public long LastPageTicks;

  /// <summary>
  /// Appends the part of the session seen in a later slice. Every event of that part is later
  /// than the events seen so far, so the first event and the session dimensions stay, and the
  /// last page view of the part, if it has one, replaces the current one.
  /// </summary>
  public void Append(RawSession later)
  {
    Last = Math.Max(Last, later.Last);
    PageViews += later.PageViews;
    Interactions += later.Interactions;
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
  /// Completes only sessions found in the requested range. A timeout limits inactivity,
  /// not total duration: each scan reaches the latest active session's next deadline.
  /// </summary>
  public void ReadContinuation(
      string site, long from, long now, DimensionSelector selector,
      Dictionary<ulong, RawSession>[] sessions, CancellationToken cancellationToken)
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

      var slices = events.Scan(site, from, to, parallelism, () => new RawScanSlice(parallelism),
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
          }, cancellationToken);
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
    var isFirstEvent = rawEvent.Ticks == session.Start && session.Dimensions == null;
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
