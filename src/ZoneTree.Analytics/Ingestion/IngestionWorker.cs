using System.Diagnostics;
using System.Threading.Channels;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Ingestion;

/// <summary>
/// A queued unit of work: an event or a flush request.
/// </summary>
readonly struct WorkItem
{
  public readonly EventSnapshot Event;

  public readonly TaskCompletionSource Flush;

  public WorkItem(EventSnapshot snapshot)
  {
    Event = snapshot;
    Flush = null;
  }

  public WorkItem(TaskCompletionSource flush)
  {
    Event = null;
    Flush = flush;
  }
}

/// <summary>
/// Components shared by all workers.
/// </summary>
sealed class IngestionServices
{
  public AnalyticsOptions Options;

  public EventEnricher Enricher;

  public RollupStore Rollups;

  public RawEventStore RawEvents;

  public IZoneTree<ulong, SessionState> Sessions;

  public ReportingCalendar Calendar;

  public RollupDefinition[] Definitions;

  public Counters Counters;

  public ILogger Logger;

  public Func<long> NextSequence;

  /// <summary>
  /// The latest LastTicks of sessions stored before the engine started, plus the timeout.
  /// </summary>
  public long StoredSessionHorizon = long.MinValue;
}

/// <summary>
/// Processes the events of a subset of visitors, in order.
/// </summary>
/// <remarks>
/// <para>
/// Events are routed to workers by visitor, so a worker exclusively owns the sessions of
/// its visitors: session state is read, updated and written without locks.
/// </para>
/// <para>
/// Raw events are written to ZoneTree immediately. Rollup rows are pre-aggregated in
/// memory and merged into ZoneTree on flush, together with the changed session states.
/// </para>
/// </remarks>
sealed class IngestionWorker
{
  readonly IngestionServices services;

  readonly Channel<WorkItem> channel;

  readonly RollupBuffer buffer;

  readonly Dictionary<ulong, SessionState> sessions = [];

  readonly HashSet<ulong> dirtySessions = [];

  readonly List<MetricChange> metricChanges = [];

  readonly List<MetricChange> metricChangesForSession = [];

  readonly long sessionTimeoutTicks;

  readonly long flushIntervalTimestamp;

  readonly bool sessionsEnabled;

  readonly bool storeRawEvents;

  readonly int maxBufferedRows;

  long watermark;

  // Events at or before this time may belong to a session that is stored but not cached
  // (stored before startup, or evicted from the cache). Later events whose visitor is not
  // cached certainly start a new session, so the session store lookup is skipped.
  long lookupHorizon;

  long lastFlushTimestamp = Stopwatch.GetTimestamp();

  PartitionLease<Memory<byte>, Memory<byte>> rawLease;

  Task task;

  public IngestionWorker(IngestionServices services)
  {
    this.services = services;
    var options = services.Options;
    channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(options.Ingestion.QueueCapacity)
    {
      SingleReader = true,
      SingleWriter = false,
      FullMode = BoundedChannelFullMode.Wait,
      AllowSynchronousContinuations = false
    });
    buffer = new RollupBuffer(
        services.Definitions,
        options.Rollups.Granularities,
        services.Calendar,
        options.Rollups.HyperLogLogPrecision);
    sessionTimeoutTicks = options.Sessions.Timeout.Ticks;
    flushIntervalTimestamp = (long)(options.Ingestion.FlushInterval.TotalSeconds * Stopwatch.Frequency);
    sessionsEnabled = options.Sessions.Enabled && services.Sessions != null;
    storeRawEvents = options.Storage.StoreRawEvents && services.RawEvents != null;
    maxBufferedRows = options.Ingestion.MaxBufferedRollupRows;
    lookupHorizon = services.StoredSessionHorizon;
  }

  public ChannelWriter<WorkItem> Writer => channel.Writer;

  public int QueueLength => channel.Reader.CanCount ? channel.Reader.Count : 0;

  public void Start()
  {
    task = Task.Run(RunAsync);
  }

  public Task Completion => task ?? Task.CompletedTask;

  async Task RunAsync()
  {
    var reader = channel.Reader;
    while (await reader.WaitToReadAsync().ConfigureAwait(false))
    {
      while (reader.TryRead(out var item))
      {
        if (item.Flush != null)
        {
          Flush();
          item.Flush.TrySetResult();
          continue;
        }
        try
        {
          Process(item.Event);
        }
        catch (Exception e)
        {
          Interlocked.Increment(ref services.Counters.Errors);
          services.Logger.LogError(e);
        }
        if (buffer.Count >= maxBufferedRows ||
            Stopwatch.GetTimestamp() - lastFlushTimestamp >= flushIntervalTimestamp)
          Flush();
      }
    }
    Flush();
  }

  void Process(EventSnapshot snapshot)
  {
    var counters = services.Counters;
    var ev = services.Enricher.Enrich(snapshot, out var reason);
    if (ev == null)
    {
      switch (reason)
      {
        case RejectReason.Bot:
          Interlocked.Increment(ref counters.BotsExcluded);
          break;
        case RejectReason.Filtered:
          Interlocked.Increment(ref counters.EventsFiltered);
          break;
        default:
          Interlocked.Increment(ref counters.EventsRejected);
          break;
      }
      return;
    }

    SessionState session = null;
    var isNewSession = false;
    if (sessionsEnabled && ev.Visitor != 0)
    {
      session = GetSession(ev.Visitor, ev.Ticks);
      if (session == null ||
          ev.Ticks > session.LastTicks + sessionTimeoutTicks ||
          ev.Ticks < session.StartTicks - sessionTimeoutTicks)
      {
        if (session != null)
          EmitSessionDeltas(session);
        session = StartSession(ev);
        sessions[ev.Visitor] = session;
        isNewSession = true;
        Interlocked.Increment(ref counters.SessionsStarted);
      }
      session.Site = ev.Site;
      dirtySessions.Add(ev.Visitor);
    }

    // Session-level dimensions come from the session, so every event of a session
    // is attributed consistently to its source, location and device.
    var dimensions = ev.Dimensions;
    var sessionDimensions = session?.Dimensions ?? ev.SessionDimensions;
    if (sessionDimensions != null)
    {
      var names = StandardDimensions.SessionLevel;
      for (var i = 0; i < sessionDimensions.Length; ++i)
      {
        if (sessionDimensions[i] != null)
          dimensions[names[i]] = sessionDimensions[i];
      }
    }
    if (session != null)
      dimensions[StandardDimensions.EntryPage] = session.EntryPage;

    if (storeRawEvents)
      WriteRawEvent(ev, session);

    metricChanges.Clear();
    if (ev.Metrics != null)
    {
      foreach (var (name, value) in ev.Metrics)
        metricChanges.Add(new MetricChange(name, value, IsDelta: false));
    }
    if (session != null)
      UpdateSession(ev, session, isNewSession, metricChanges);
    buffer.Contribute(ev.Site, ev.Name, ev.Ticks, dimensions, 1, ev.Visitor, metricChanges);

    if (ev.Ticks > watermark)
      watermark = ev.Ticks;
    Interlocked.Increment(ref counters.EventsProcessed);
  }

  SessionState GetSession(ulong visitor, long ticks)
  {
    if (sessions.TryGetValue(visitor, out var session))
      return session;
    if (ticks > lookupHorizon)
      return null;
    if (services.Sessions.TryGet(visitor, out var stored) && stored != null)
    {
      // The stored instance may be referenced by a ZoneTree segment: work on a copy.
      session = stored.Clone();
      session.ClearPendingDeltas();
      sessions[visitor] = session;
      return session;
    }
    return null;
  }

  static SessionState StartSession(EnrichedEvent ev)
  {
    var dimensions = ev.SessionDimensions != null
        ? (string[])ev.SessionDimensions.Clone()
        : new string[StandardDimensions.SessionLevel.Count];
    return new SessionState
    {
      SessionId = (ulong)Random.Shared.NextInt64() | 1UL,
      StartTicks = ev.Ticks,
      LastTicks = ev.Ticks,
      EntryPage = ev.Page ?? "",
      EntryHostname = ev.Hostname ?? "",
      Dimensions = dimensions
    };
  }

  static Dictionary<string, string> GetSessionDimensions(SessionState session)
  {
    if (session.DimensionCache != null)
      return session.DimensionCache;
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var names = StandardDimensions.SessionLevel;
    for (var i = 0; i < session.Dimensions.Length; ++i)
    {
      if (session.Dimensions[i] != null)
        result[names[i]] = session.Dimensions[i];
    }
    result[StandardDimensions.EntryPage] = session.EntryPage;
    session.DimensionCache = result;
    return result;
  }

  static Dictionary<string, string> WithPage(
      Dictionary<string, string> sessionDimensions, string page, string hostname)
  {
    return new Dictionary<string, string>(sessionDimensions, StringComparer.Ordinal)
    {
      [StandardDimensions.Page] = page,
      [StandardDimensions.Hostname] = hostname ?? ""
    };
  }

  /// <summary>
  /// Applies an event to its session.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Session metrics are additive deltas attributed to the session start bucket: a session
  /// counts +1 when it starts, its bounce flips from 1 to 0 when a second interactive event
  /// arrives, and its duration grows with every event. Deltas are accumulated on the session
  /// and emitted once per flush, so an active session costs one rollup update per flush
  /// rather than one per event. Because all of these are sums, rollups stay correct and
  /// real-time without session-close processing.
  /// </para>
  /// <para>
  /// Entrances and exits are attributed to the page view they belong to. The exit moves with
  /// every page view: -1 on the previous last page view, +1 on the new one. When the page view
  /// has no custom dimensions, the +1 exit and the entrance are merged into the page view's
  /// own rollup contribution.
  /// </para>
  /// </remarks>
  void UpdateSession(EnrichedEvent ev, SessionState session, bool isNewSession, List<MetricChange> eventMetrics)
  {
    var wasBounce = !isNewSession && session.IsBounce;
    var durationDelta = isNewSession ? 0 : Math.Max(0, ev.Ticks - session.LastTicks);
    if (ev.Ticks > session.LastTicks)
      session.LastTicks = ev.Ticks;
    ++session.Events;
    if (ev.Interactive)
      ++session.Interactions;
    if (ev.IsPageView)
      ++session.PageViews;

    if (isNewSession)
    {
      session.PendingStart = true;
      session.PendingVisitor = ev.Visitor;
    }
    session.PendingBounces += (session.IsBounce ? 1 : 0) - (wasBounce ? 1 : 0);
    session.PendingDurationTicks += durationDelta;
    if (ev.IsPageView)
      ++session.PendingPageViews;

    if (!ev.IsPageView || ev.Page == null)
      return;
    if (session.LastPage != null && ev.Ticks < session.LastPageTicks)
      return; // A late page view does not become the exit page.

    var sessionDimensions = GetSessionDimensions(session);
    if (session.LastPage != null)
    {
      metricChangesForSession.Clear();
      metricChangesForSession.Add(new MetricChange(StandardMetrics.Exits, -1, IsDelta: true));
      buffer.Contribute(
          ev.Site,
          StandardEvents.PageView,
          session.LastPageTicks,
          WithPage(sessionDimensions, session.LastPage, session.LastHostname),
          0,
          0,
          metricChangesForSession,
          DimensionScope.PageView);
    }
    var isEntrance = isNewSession && session.Events == 1;
    if (ev.HasCustomDimensions)
    {
      // Entrances and exits never carry custom dimensions: contribute them separately.
      metricChangesForSession.Clear();
      metricChangesForSession.Add(new MetricChange(StandardMetrics.Exits, 1, IsDelta: true));
      if (isEntrance)
        metricChangesForSession.Add(new MetricChange(StandardMetrics.Entrances, 1, IsDelta: true));
      buffer.Contribute(
          ev.Site,
          StandardEvents.PageView,
          ev.Ticks,
          WithPage(sessionDimensions, ev.Page, ev.Hostname),
          0,
          0,
          metricChangesForSession,
          DimensionScope.PageView);
    }
    else
    {
      eventMetrics.Add(new MetricChange(StandardMetrics.Exits, 1, IsDelta: true));
      if (isEntrance)
        eventMetrics.Add(new MetricChange(StandardMetrics.Entrances, 1, IsDelta: true));
    }
    session.LastPage = ev.Page;
    session.LastHostname = ev.Hostname ?? "";
    session.LastPageTicks = ev.Ticks;
  }

  /// <summary>
  /// Emits the session deltas accumulated since the last flush.
  /// </summary>
  void EmitSessionDeltas(SessionState session)
  {
    if (!session.HasPendingDeltas || session.Site == null)
      return;
    metricChangesForSession.Clear();
    if (session.PendingBounces != 0)
      metricChangesForSession.Add(new MetricChange(StandardMetrics.Bounces, session.PendingBounces, IsDelta: true));
    if (session.PendingDurationTicks != 0)
    {
      metricChangesForSession.Add(new MetricChange(
          StandardMetrics.Duration, (double)session.PendingDurationTicks / TimeSpan.TicksPerSecond, IsDelta: true));
    }
    if (session.PendingPageViews != 0)
      metricChangesForSession.Add(new MetricChange(StandardMetrics.SessionPageViews, session.PendingPageViews, IsDelta: true));
    buffer.Contribute(
        session.Site,
        StandardEvents.Session,
        session.StartTicks,
        GetSessionDimensions(session),
        session.PendingStart ? 1 : 0,
        session.PendingStart ? session.PendingVisitor : 0,
        metricChangesForSession,
        DimensionScope.Session);
    session.ClearPendingDeltas();
  }

  void WriteRawEvent(EnrichedEvent ev, SessionState session)
  {
    var store = services.RawEvents.Store;
    var partition = store.Scheme.NameOf(ev.Ticks);
    if (rawLease == null || rawLease.Name != partition)
    {
      rawLease?.Dispose();
      rawLease = null;
      rawLease = store.Acquire(partition, create: true);
      if (rawLease == null)
        return; // The partition is being dropped by retention.
    }
    var key = KeyCodec.RawKey(ev.Site, ev.Ticks, services.NextSequence());
    var value = RawEventCodec.Encode(
        ev.Name,
        ev.Ticks,
        ev.Visitor,
        session?.SessionId ?? 0,
        session?.StartTicks ?? 0,
        ev.Interactive,
        ev.Dimensions,
        ev.Metrics);
    rawLease.Tree.Upsert(key, value);
  }

  void Flush()
  {
    lastFlushTimestamp = Stopwatch.GetTimestamp();
    try
    {
      foreach (var visitor in dirtySessions)
      {
        if (sessions.TryGetValue(visitor, out var dirty))
          EmitSessionDeltas(dirty);
      }
      var rows = buffer.Swap();
      services.Rollups.Write(rows);

      if (dirtySessions.Count > 0)
      {
        var expiresAt = DateTime.UtcNow.Ticks + sessionTimeoutTicks * 2;
        foreach (var visitor in dirtySessions)
        {
          if (!sessions.TryGetValue(visitor, out var session))
            continue;
          var snapshot = session.Clone();
          snapshot.ExpiresAtUtcTicks = expiresAt;
          services.Sessions.Upsert(visitor, snapshot);
        }
        dirtySessions.Clear();
      }
      EvictSessions();
      Interlocked.Increment(ref services.Counters.Flushes);
    }
    catch (Exception e)
    {
      Interlocked.Increment(ref services.Counters.Errors);
      services.Logger.LogError(e);
    }
    finally
    {
      // Release the raw partition so idle partitions can close and retention can drop them.
      rawLease?.Dispose();
      rawLease = null;
    }
  }

  void EvictSessions()
  {
    if (sessions.Count == 0)
      return;
    var expiredBefore = watermark - sessionTimeoutTicks;
    List<ulong> expired = null;
    foreach (var (visitor, session) in sessions)
    {
      if (session.LastTicks < expiredBefore)
      {
        (expired ??= []).Add(visitor);
        lookupHorizon = Math.Max(lookupHorizon, session.LastTicks + sessionTimeoutTicks);
      }
    }
    if (expired != null)
    {
      foreach (var visitor in expired)
        sessions.Remove(visitor);
    }
    var max = services.Options.Sessions.MaxCachedSessions;
    if (sessions.Count <= max)
      return;
    // All sessions are persisted at this point; drop the least recently active quarter.
    var ordered = sessions.OrderBy(x => x.Value.LastTicks).Take(sessions.Count - max * 3 / 4).ToList();
    foreach (var (visitor, session) in ordered)
    {
      lookupHorizon = Math.Max(lookupHorizon, session.LastTicks + sessionTimeoutTicks);
      sessions.Remove(visitor);
    }
  }
}
