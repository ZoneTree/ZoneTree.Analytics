using System.Globalization;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics
{
  /// <summary>
  /// One step of a funnel: an event, optionally restricted by filters.
  /// </summary>
  public sealed class FunnelStep
  {
    /// <summary>
    /// Creates a step.
    /// </summary>
    /// <param name="name">The label shown in results.</param>
    /// <param name="eventName">The event that completes the step.</param>
    /// <param name="filters">Conditions on that event. All must match.</param>
    public FunnelStep(string name, string eventName, params Filter[] filters)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(name);
      ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
      Name = name;
      Event = eventName;
      Filters = filters ?? [];
    }

    /// <summary>The label.</summary>
    public string Name { get; }

    /// <summary>The event that completes the step.</summary>
    public string Event { get; }

    /// <summary>Conditions on the event.</summary>
    public IReadOnlyList<Filter> Filters { get; }

    /// <summary>
    /// A page view of a page.
    /// </summary>
    public static FunnelStep Page(string name, string page) =>
        new(name, StandardEvents.PageView, Filter.Equal(StandardDimensions.Page, page));

    /// <summary>
    /// A custom event.
    /// </summary>
    public static FunnelStep Goal(string eventName, params Filter[] filters) => new(eventName, eventName, filters);

    internal bool Matches(RawEvent rawEvent)
    {
      if (rawEvent.Name != Event)
        return false;
      foreach (var filter in Filters)
      {
        rawEvent.TryGetDimension(filter.Dimension, out var value);
        if (!filter.IsMatch(value))
          return false;
      }
      return true;
    }
  }

  /// <summary>
  /// A funnel: an ordered sequence of steps a visitor completes, within one session or within
  /// a time window from the first step.
  /// </summary>
  /// <remarks>
  /// Funnels are evaluated over raw events, so they are available within raw event retention.
  /// Steps must happen in order; other events may happen in between.
  /// </remarks>
  public sealed class FunnelQuery
  {
    /// <summary>The site. Defaults to <see cref="AnalyticsOptions.DefaultSite"/>.</summary>
    public string Site { get; set; }

    /// <summary>The time range. Default is the last 30 days including today.</summary>
    public DateRange Range { get; set; } = DateRange.LastDays(30);

    /// <summary>The time zone of calendar ranges. Defaults to the engine's reporting time zone.</summary>
    public TimeZoneInfo TimeZone { get; set; }

    /// <summary>The steps, in order. At least two.</summary>
    public List<FunnelStep> Steps { get; } = [];

    /// <summary>
    /// The time allowed from the first step to the last. Null (the default) requires all steps
    /// within one session.
    /// </summary>
    public TimeSpan? Window { get; set; }

    /// <summary>Filters on every event considered, for example a country.</summary>
    public List<Filter> Filters { get; } = [];

    /// <summary>Behavioral segments restricting the visitors or sessions considered.</summary>
    public List<Segment> Segments { get; } = [];
  }

  /// <summary>
  /// The result of one funnel step.
  /// </summary>
  /// <param name="Name">The step label.</param>
  /// <param name="Visitors">Unique visitors who reached the step.</param>
  /// <param name="ConversionRate">Visitors of this step divided by visitors of the first step, 0 to 1.</param>
  /// <param name="StepConversionRate">Visitors of this step divided by visitors of the previous step, 0 to 1.</param>
  /// <param name="DropOff">Visitors of the previous step who did not reach this step.</param>
  public sealed record FunnelStepResult(
      string Name,
      long Visitors,
      double ConversionRate,
      double StepConversionRate,
      long DropOff);

  /// <summary>
  /// The result of a funnel query.
  /// </summary>
  public sealed class FunnelResult
  {
    internal FunnelResult(
        IReadOnlyList<FunnelStepResult> steps, DateTimeOffset from, DateTimeOffset to, QueryPlan plan)
    {
      Steps = steps;
      From = from;
      To = to;
      Plan = plan;
    }

    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<FunnelStepResult> Steps { get; }

    /// <summary>Visitors who completed every step divided by visitors of the first step.</summary>
    public double ConversionRate => Steps.Count == 0 ? 0 : Steps[^1].ConversionRate;

    /// <summary>The resolved inclusive start of the range.</summary>
    public DateTimeOffset From { get; }

    /// <summary>The resolved exclusive end of the range.</summary>
    public DateTimeOffset To { get; }

    /// <summary>How the funnel was answered.</summary>
    public QueryPlan Plan { get; }
  }
}

namespace ZoneTree.Analytics.Query
{
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

    sealed class Progress
    {
      public ulong Visitor;

      public int Current;

      public int Best;

      public long Start;

      public long LastStart;
    }

    public FunnelResult Execute(FunnelQuery funnel, CancellationToken cancellationToken)
    {
      var steps = funnel.Steps.Where(x => x != null).ToArray();
      if (steps.Length < 2)
        throw new AnalyticsQueryException("A funnel needs at least two steps.");
      if (funnel.Window is { } w && w <= TimeSpan.Zero)
        throw new AnalyticsQueryException("The funnel window must be positive.");
      if (!options.Storage.StoreRawEvents || rawEvents == null)
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
      var retention = options.Retention.RawEvents;
      if (retention != null && from < now - retention.Value.Ticks)
      {
        warnings.Add(
            $"Raw events before {new DateTime(now - retention.Value.Ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} " +
            "are outside retention; the result may be incomplete.");
      }

      // Behavioral segments reuse the query engine's semantics through a pre-pass.
      HashSet<ulong>[] segmentMembers = null;
      var segments = funnel.Segments.Where(x => x != null).ToArray();
      var rawEvent = new RawEvent();
      var counter = 0;
      string Lookup(string dimension) => rawEvent.TryGetDimension(dimension, out var value) ? value : null;
      if (segments.Length > 0)
      {
        segmentMembers = segments.Select(_ => new HashSet<ulong>()).ToArray();
        var segmentSelector = new DimensionSelector(segments.SelectMany(x => x.Filters).Select(x => x.Dimension));
        rawEvents.Scan(site, from, to, (ticks, _, value) =>
        {
          if (++counter % 1024 == 0)
            cancellationToken.ThrowIfCancellationRequested();
          if (!RawEventCodec.TryDecode(value.Span, ticks, null, default, rawEvent, segmentSelector))
            return true;
          for (var i = 0; i < segments.Length; ++i)
          {
            var id = segments[i].Scope == SegmentScope.Visitors ? rawEvent.Visitor : rawEvent.SessionId;
            if (id != 0 && segments[i].Matches(rawEvent.Name, Lookup))
              segmentMembers[i].Add(id);
          }
          return true;
        });
      }
      bool Allowed()
      {
        if (segmentMembers == null)
          return true;
        for (var i = 0; i < segments.Length; ++i)
        {
          var id = segments[i].Scope == SegmentScope.Visitors ? rawEvent.Visitor : rawEvent.SessionId;
          var isMember = id != 0 && segmentMembers[i].Contains(id);
          if (isMember == segments[i].IsExcluded)
            return false;
        }
        return true;
      }

      var selector = new DimensionSelector(
          filters.Select(x => x.Dimension).Concat(steps.SelectMany(x => x.Filters).Select(x => x.Dimension)));
      var bySession = funnel.Window == null;
      var windowTicks = funnel.Window?.Ticks ?? 0;
      var progress = new Dictionary<ulong, Progress>();
      rawEvents.Scan(site, from, to, (ticks, _, value) =>
      {
        if (++counter % 1024 == 0)
          cancellationToken.ThrowIfCancellationRequested();
        if (!RawEventCodec.TryDecode(value.Span, ticks, null, default, rawEvent, selector))
          return true;
        if (rawEvent.Visitor == 0)
          return true;
        var key = bySession ? rawEvent.SessionId : rawEvent.Visitor;
        if (key == 0 || !Allowed())
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
        Advance(state, steps, rawEvent, ticks, bySession ? 0 : windowTicks);
        return true;
      });

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
            i == 0 ? 0 : reached[i - 1] - reached[i]));
      }
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
      var isFirstStep = steps[0].Matches(rawEvent);
      if (isFirstStep)
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
}
