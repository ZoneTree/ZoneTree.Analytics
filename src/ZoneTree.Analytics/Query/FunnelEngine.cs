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

    /// <summary>The latest first-step time of each unfinished prefix, for window funnels.</summary>
    public long[] Starts;
  }

  public FunnelResult Execute(FunnelQuery funnel, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(funnel);
    var steps = funnel.Steps.Where(x => x != null).ToArray();
    if (steps.Length < 2)
      throw new AnalyticsQueryException("A funnel needs at least two steps.");
    if (steps.Length > FunnelQuery.MaxSteps)
      throw new AnalyticsQueryException($"A funnel has at most {FunnelQuery.MaxSteps} steps.");
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
        : SegmentSets.Evaluate(
            rawEvents, site, from, to, segments, calendar, options.Queries.MaxDegreeOfParallelism, cancellationToken);

    var progress = Scan(site, from, to, steps, filters, segmentSets, calendar, funnel.Window, cancellationToken, out var index);
    var results = Summarize(steps, progress);
    var plan = new QueryPlan(
        steps.Select(x => x.Event).Distinct().Select(x => new QueryPlanStep(
            x, index == null ? PlanSource.RawEvents : PlanSource.DimensionIndex,
            new DateTimeOffset(from, TimeSpan.Zero), new DateTimeOffset(to, TimeSpan.Zero))
            { IndexDimension = index?.Index.Dimension }).ToList(),
        warnings);
    return new FunnelResult(
        results,
        TimeZoneInfo.ConvertTime(new DateTimeOffset(from, TimeSpan.Zero), calendar.TimeZone),
        TimeZoneInfo.ConvertTime(new DateTimeOffset(to, TimeSpan.Zero), calendar.TimeZone),
        plan);
  }

  /// <summary>
  /// An event that matches at least one step, with the steps it matches as bits.
  /// </summary>
  readonly record struct StepHit(ulong Key, ulong Visitor, long Ticks, ulong Steps);

  /// <summary>
  /// The step hits of one slice, by shard of the progress key.
  /// </summary>
  sealed class FunnelSlice(int shardCount)
  {
    public readonly RawEvent RawEvent = new();

    public readonly List<StepHit>[] Hits = Shards.Create<List<StepHit>>(shardCount);
  }

  /// <summary>
  /// Follows every session (or, with a window, every visitor) through the steps.
  /// </summary>
  /// <remarks>
  /// Slices are scanned in parallel and keep only the events that match a step: an event that
  /// matches no step does not change any progress. The hits of each shard are then replayed in
  /// time order, shards in parallel.
  /// </remarks>
  Dictionary<ulong, Progress>[] Scan(
      string site,
      long from,
      long to,
      FunnelStep[] steps,
      Filter[] filters,
      SegmentSets segmentSets,
      ReportingCalendar calendar,
      TimeSpan? window,
      CancellationToken cancellationToken, out DimensionIndexSelection usedIndex)
  {
    var selector = new DimensionSelector(
        filters.SelectMany(x => x.Dimensions).Concat(steps.SelectMany(x => x.Filters).Where(x => x != null).SelectMany(x => x.Dimensions)));
    var bySession = window == null;
    var windowTicks = window?.Ticks ?? 0;
    var maxDegree = options.Queries.MaxDegreeOfParallelism;
    var shardCount = maxDegree;
    var index = rawEvents.Indexes?.Select(site, from, to, filters, _ => true, cancellationToken);
    var slices = rawEvents.ScanIndexed(
        index, site, from, to, maxDegree, () => new FunnelSlice(shardCount),
        (slice, ticks, value) =>
        {
          var rawEvent = slice.RawEvent;
          if (!RawEventCodec.TryDecode(value, ticks, null, default, rawEvent, selector))
            return;
          if (rawEvent.Visitor == 0)
            return;
          var key = bySession ? rawEvent.SessionId : rawEvent.Visitor;
          if (key == 0 || (segmentSets != null && !segmentSets.Allows(rawEvent.Visitor, rawEvent.SessionId)))
            return;
          var dimensions = new FilterValues(rawEvent, calendar);
          foreach (var filter in filters)
          {
            if (!filter.IsMatch(dimensions))
              return;
          }
          ulong matched = 0;
          for (var i = 0; i < steps.Length; ++i)
          {
            if (steps[i].Matches(rawEvent, calendar))
              matched |= 1UL << i;
          }
          if (matched != 0)
            slice.Hits[Shards.Of(key, shardCount)].Add(new StepHit(key, rawEvent.Visitor, ticks, matched));
        },
        cancellationToken, out var indexed);
    usedIndex = indexed ? index : null;

    var progress = new Dictionary<ulong, Progress>[shardCount];
    ParallelWork.For(shardCount, maxDegree, shard =>
    {
      var progressOfShard = new Dictionary<ulong, Progress>();
      foreach (var slice in slices)
      {
        foreach (var hit in slice.Hits[shard])
        {
          if (!progressOfShard.TryGetValue(hit.Key, out var state))
          {
            if ((hit.Steps & 1) == 0)
              continue;
            state = new Progress { Visitor = hit.Visitor };
            progressOfShard.Add(hit.Key, state);
          }
          Advance(state, steps.Length, hit.Steps, hit.Ticks, windowTicks);
        }
      }
      progress[shard] = progressOfShard;
    });
    return progress;
  }

  /// <summary>
  /// Counts each visitor once, at the furthest step any of its sessions or attempts reached.
  /// </summary>
  static List<FunnelStepResult> Summarize(FunnelStep[] steps, Dictionary<ulong, Progress>[] progress)
  {
    var bestByVisitor = new Dictionary<ulong, int>();
    foreach (var state in progress.SelectMany(x => x.Values))
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
  /// Advances a visitor's (or session's) progress with one event that matches the steps in
  /// <paramref name="matchedSteps"/>, one bit per step. Steps are completed in order.
  /// Window funnels retain the latest start of every completed prefix. Among attempts at the
  /// same step, that start dominates older ones: it allows every future completion they do.
  /// Prefixes advance in reverse order so one event never completes two steps.
  /// </summary>
  static void Advance(Progress state, int stepCount, ulong matchedSteps, long ticks, long windowTicks)
  {
    if (state.Best == stepCount)
      return;
    if (windowTicks > 0)
    {
      if (state.Starts == null)
      {
        state.Starts = new long[stepCount - 1];
        Array.Fill(state.Starts, long.MinValue);
      }
      for (var step = stepCount - 1; step > 0; --step)
      {
        var start = state.Starts[step - 1];
        if (((matchedSteps >> step) & 1) == 0 || start == long.MinValue || ticks - start > windowTicks)
          continue;
        if (step < stepCount - 1)
          state.Starts[step] = Math.Max(state.Starts[step], start);
        state.Best = Math.Max(state.Best, step + 1);
      }
      if ((matchedSteps & 1) != 0)
      {
        state.Starts[0] = ticks;
        state.Best = Math.Max(state.Best, 1);
      }
      return;
    }
    if (((matchedSteps >> state.Current) & 1) != 0)
    {
      ++state.Current;
      state.Best = Math.Max(state.Best, state.Current);
    }
  }
}
