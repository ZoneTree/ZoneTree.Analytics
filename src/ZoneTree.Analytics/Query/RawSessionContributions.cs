using ZoneTree.Analytics.Ingestion;

namespace ZoneTree.Analytics.Query;

/// <summary>
/// What a reconstructed session contributes to rollups: its session row, and the entrance and
/// exit of its page views. Rollups rebuilt from raw events and queries of raw events both take
/// them from here; ingestion maintains the same values incrementally, as deltas.
/// </summary>
static class RawSessionContributions
{
  /// <summary>
  /// The values of the session row, in place of the list's contents: a bounce when the session
  /// has at most one interaction, its duration and its page views. Zero deltas are omitted, as
  /// ingestion omits them.
  /// </summary>
  public static void SessionValues(RawSession session, List<ValueChange> values)
  {
    values.Clear();
    if (session.Interactions <= 1)
      values.Add(new ValueChange(StandardValues.Bounces, 1, IsDelta: true));
    var duration = session.Last - session.Start;
    if (duration > 0)
      values.Add(new ValueChange(StandardValues.Duration, (double)duration / TimeSpan.TicksPerSecond, IsDelta: true));
    if (session.PageViews > 0)
      values.Add(new ValueChange(StandardValues.SessionPageViews, session.PageViews, IsDelta: true));
  }

  /// <summary>The entrance: the first event of the session, when it is a page view.</summary>
  public static bool Entrance(RawSession session, out long ticks, out string page, out string hostname)
  {
    var entered = session.FirstIsPageView && session.FirstPage != null;
    ticks = entered ? session.Start : 0;
    page = entered ? session.FirstPage : null;
    hostname = entered ? session.FirstHostname : null;
    return entered;
  }

  /// <summary>The exit: the last page view of the session.</summary>
  public static bool Exit(RawSession session, out long ticks, out string page, out string hostname)
  {
    var exited = session.LastPage != null;
    ticks = exited ? session.LastPageTicks : 0;
    page = exited ? session.LastPage : null;
    hostname = exited ? session.LastHostname : null;
    return exited;
  }
}
