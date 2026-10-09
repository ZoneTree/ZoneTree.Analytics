using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics;

/// <summary>
/// One step of a funnel: an event, optionally restricted by filters.
/// </summary>
public sealed class FunnelStep
{
  readonly Filter[] filters;

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
    this.filters = filters == null ? [] : [.. filters];
  }

  /// <summary>The label.</summary>
  public string Name { get; }

  /// <summary>The event that completes the step.</summary>
  public string Event { get; }

  /// <summary>Conditions on the event.</summary>
  public IReadOnlyList<Filter> Filters => filters;

  /// <summary>
  /// A page view of a page.
  /// </summary>
  public static FunnelStep Page(string name, string page) =>
      new(name, StandardEvents.PageView, Filter.Equal(StandardDimensions.Page, page));

  /// <summary>
  /// A custom event.
  /// </summary>
  public static FunnelStep Goal(string eventName, params Filter[] filters) => new(eventName, eventName, filters);

  internal bool Matches(RawEvent rawEvent) => rawEvent.Matches(Event, filters);
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
  /// <summary>
  /// The most steps a funnel can have.
  /// </summary>
  public const int MaxSteps = 64;

  /// <summary>The site. Defaults to <see cref="AnalyticsOptions.DefaultSite"/>.</summary>
  public string Site { get; set; }

  /// <summary>The time range. Default is the last 30 days including today.</summary>
  public DateRange Range { get; set; } = DateRange.LastDays(30);

  /// <summary>The time zone of calendar ranges. Defaults to the engine's reporting time zone.</summary>
  public TimeZoneInfo TimeZone { get; set; }

  /// <summary>The steps, in order. At least two, at most <see cref="MaxSteps"/>.</summary>
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
