using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>
/// Evaluates funnels over raw events.
/// </summary>
sealed class FunnelEngine
{
  readonly AnalyticsOptions options;

  readonly ReportingCalendar storageCalendar;

  readonly RawEventStore rawEvents;

  readonly EnrichmentPipeline enricher;

  public FunnelEngine(
      AnalyticsOptions options, ReportingCalendar storageCalendar, RawEventStore rawEvents, EnrichmentPipeline enricher)
  {
    this.options = options;
    this.storageCalendar = storageCalendar;
    this.rawEvents = rawEvents;
    this.enricher = enricher;
  }

  /// <summary>
  /// The progress of one visitor (or session) through the steps.
  /// </summary>
  sealed class Progress
  {
    public ulong Visitor;

    /// <summary>The number of steps completed in the current attempt.</summary>
    public int Current;

    /// <summary>The most steps completed in any attempt.</summary>
    public int Best;

    /// <summary>When the current attempt completed its first step.</summary>
    public long Start;

    /// <summary>When the first step was last matched.</summary>
    public long LastStart;
  }

  public FunnelResult Execute(FunnelQuery funnel, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(funnel);
    var steps = funnel.Steps.Where(x => x != null).ToArray();
    if (steps.Length < 2)
      throw new AnalyticsQueryException("A funnel needs at least two steps.");
    if (funnel.Window <= TimeSpan.Zero)
      throw new AnalyticsQueryException("The funnel window must be positive.");
    if (rawEvents == null)
      throw new AnalyticsQueryException("Funnels require raw events (StorageOptions.StoreRawEvents).");
    if (funnel.Window == null && !options.Sessions.Enabled)
      throw new AnalyticsQueryException("Session funnels require sessions; set a Window or enable sessions.");

    var calendar = funnel.TimeZone == null || funnel.TimeZone.Id == storageCalendar.TimeZone.Id
        ? storageCalendar
        : new ReportingCalendar(funnel.TimeZone, options.FirstDayOfWeek);
    var now = DateTime.UtcNow.Ticks;
    var (from, to) = funnel.Range.Resolve(calendar, now);
    var site = enricher.NormalizeSite(funnel.Site);
    var filters = funnel.Filters.Where(x => x != null).ToArray();
    var warnings = new List<string>();
    if (RetentionCutoffs.Compute(options.Retention, storageCalendar, now).RawEventsWarning(from) is { } warning)
      warnings.Add(warning);

    var segments = funnel.Segments.Where(x => x != null).ToArray();
    var segmentSets = segments.Length == 0
        ? null
        : SegmentSets.Evaluate(rawEvents, site, from, to, segments, cancellationToken);

    var progress = Scan(site, from, to, steps, filters, segmentSets, funnel.Window, cancellationToken);
    var results = Summarize(steps, progress);
    var plan = new QueryPlan(
        steps.Select(x => x.Event).Distinct().Select(x => new QueryPlanStep(
            x, PlanSource.RawEvents, new DateTimeOffset(from, TimeSpan.Zero), new DateTimeOffset(to, TimeSpan.Zero))).ToList(),
        warnings);
    return new FunnelResult(
        results,
        TimeZoneInfo.ConvertTime(new DateTimeOffset(from, TimeSpan.Zero), calendar.TimeZone),
        TimeZoneInfo.ConvertTime(new DateTimeOffset(to, TimeSpan.Zero), calendar.TimeZone),
        plan);
  }

  /// <summary>
  /// Follows every session (or, with a window, every visitor) through the steps.
  /// </summary>
  Dictionary<ulong, Progress> Scan(
      string site,
      long from,
      long to,
      FunnelStep[] steps,
      Filter[] filters,
      SegmentSets segmentSets,
      TimeSpan? window,
      CancellationToken cancellationToken)
  {
    var selector = new DimensionSelector(
        filters.Select(x => x.Dimension).Concat(steps.SelectMany(x => x.Filters).Select(x => x.Dimension)));
    var bySession = window == null;
    var windowTicks = window?.Ticks ?? 0;
    var progress = new Dictionary<ulong, Progress>();
    var rawEvent = new RawEvent();
    var counter = 0;
    rawEvents.Scan(site, from, to, (ticks, _, value) =>
    {
      if (++counter % SegmentSets.CancellationCheckInterval == 0)
        cancellationToken.ThrowIfCancellationRequested();
      if (!RawEventCodec.TryDecode(value.Span, ticks, null, default, rawEvent, selector))
        return true;
      if (rawEvent.Visitor == 0)
        return true;
      var key = bySession ? rawEvent.SessionId : rawEvent.Visitor;
      if (key == 0 || (segmentSets != null && !segmentSets.Allows(rawEvent.Visitor, rawEvent.SessionId)))
        return true;
      foreach (var filter in filters)
      {
        rawEvent.TryGetDimension(filter.Dimension, out var dimension);
        if (!filter.IsMatch(dimension))
          return true;
      }
      if (!progress.TryGetValue(key, out var state))
      {
        if (!steps[0].Matches(rawEvent))
          return true;
        state = new Progress { Visitor = rawEvent.Visitor };
        progress.Add(key, state);
      }
      Advance(state, steps, rawEvent, ticks, windowTicks);
      return true;
    });
    return progress;
  }

  /// <summary>
  /// Counts each visitor once, at the furthest step any of its sessions or attempts reached.
  /// </summary>
  static List<FunnelStepResult> Summarize(FunnelStep[] steps, Dictionary<ulong, Progress> progress)
  {
    var bestByVisitor = new Dictionary<ulong, int>();
    foreach (var state in progress.Values)
    {
      if (!bestByVisitor.TryGetValue(state.Visitor, out var best) || state.Best > best)
        bestByVisitor[state.Visitor] = state.Best;
    }
    var reached = new long[steps.Length];
    foreach (var best in bestByVisitor.Values)
    {
      for (var i = 0; i < best; ++i)
        ++reached[i];
    }
    var results = new List<FunnelStepResult>(steps.Length);
    for (var i = 0; i < steps.Length; ++i)
    {
      var previous = i == 0 ? reached[0] : reached[i - 1];
      results.Add(new FunnelStepResult(
          steps[i].Name,
          reached[i],
          reached[0] == 0 ? 0 : (double)reached[i] / reached[0],
          previous == 0 ? 0 : (double)reached[i] / previous,
          previous - reached[i]));
    }
    return results;
  }

  /// <summary>
  /// Advances a visitor's (or session's) progress with one event. Steps are completed in order.
  /// With a time window, an attempt that runs out of time restarts from the latest first step
  /// that is still inside the window.
  /// </summary>
  static void Advance(Progress state, FunnelStep[] steps, RawEvent rawEvent, long ticks, long windowTicks)
  {
    if (windowTicks > 0 && state.Current > 0 && ticks - state.Start > windowTicks)
    {
      if (ticks - state.LastStart <= windowTicks)
      {
        state.Start = state.LastStart;
        state.Current = 1;
      }
      else
      {
        state.Current = 0;
      }
    }
    if (steps[0].Matches(rawEvent))
      state.LastStart = ticks;
    if (state.Current == steps.Length)
      return;
    if (steps[state.Current].Matches(rawEvent))
    {
      if (state.Current == 0)
        state.Start = ticks;
      ++state.Current;
      state.Best = Math.Max(state.Best, state.Current);
    }
  }
}
