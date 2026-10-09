using System.Globalization;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics;

/// <summary>
/// A value a query computes for every row.
/// </summary>
/// <remarks>
/// <para>
/// Metrics read one or more events. <see cref="Events"/>, <see cref="Visitors"/> and value
/// metrics such as <see cref="Sum(string, string)"/> read the query's base event unless an event
/// is given. Session metrics read sessions, <see cref="PageViews"/>, <see cref="Entrances"/> and
/// <see cref="Exits"/> read page views, and goal metrics read their goal event. Rates are
/// fractions between 0 and 1; durations are seconds.
/// </para>
/// <para>
/// Session metrics can only be grouped and filtered by session-level dimensions and the entry
/// page; entrance and exit metrics additionally by page and hostname. Use a
/// <see cref="Segment"/> to select sessions by what happened in them.
/// </para>
/// <para>
/// Every metric has a stable <see cref="Key"/>, which <see cref="Parse"/> accepts, for example
/// "visitors", "bounce_rate", "sum:revenue" or "conversion_rate:signup".
/// </para>
/// </remarks>
public sealed class Metric : IEquatable<Metric>
{
  internal enum Kind
  {
    Events,
    Visitors,
    Sessions,
    Bounces,
    BounceRate,
    SessionDuration,
    AverageSessionDuration,
    PageViewsPerSession,
    Entrances,
    Exits,
    ExitRate,
    Sum,
    Average,
    Min,
    Max,
    Count,
    Conversions,
    ConversionRate,
    PercentOfTotal,
  }

  Metric(Kind kind, string key, string argument = null, string eventName = null, Metric inner = null)
  {
    MetricKind = kind;
    Key = key;
    Argument = argument;
    EventName = eventName;
    Inner = inner;
  }

  internal Kind MetricKind { get; }

  /// <summary>The metric name for value metrics, or the goal event for goal metrics.</summary>
  internal string Argument { get; }

  /// <summary>An explicit event for count, visitor and value metrics; null means the base event.</summary>
  internal string EventName { get; }

  internal Metric Inner { get; }

  /// <summary>
  /// The stable identifier of the metric.
  /// </summary>
  public string Key { get; }

  /// <summary>Number of base events. For the session event, the number of sessions.</summary>
  public static Metric Events { get; } = new(Kind.Events, "events");

  /// <summary>Unique visitors of the base event.</summary>
  public static Metric Visitors { get; } = new(Kind.Visitors, "visitors");

  /// <summary>Number of page views.</summary>
  public static Metric PageViews { get; } = new(Kind.Events, "pageviews", eventName: StandardEvents.PageView);

  /// <summary>Sessions started in the range.</summary>
  public static Metric Sessions { get; } = new(Kind.Sessions, "sessions");

  /// <summary>Sessions with at most one interactive event.</summary>
  public static Metric Bounces { get; } = new(Kind.Bounces, "bounces");

  /// <summary>Bounces per session, 0 to 1.</summary>
  public static Metric BounceRate { get; } = new(Kind.BounceRate, "bounce_rate");

  /// <summary>Total session duration in seconds.</summary>
  public static Metric SessionDuration { get; } = new(Kind.SessionDuration, "session_duration");

  /// <summary>Average session duration in seconds.</summary>
  public static Metric AverageSessionDuration { get; } = new(Kind.AverageSessionDuration, "avg_session_duration");

  /// <summary>Average page views per session.</summary>
  public static Metric PageViewsPerSession { get; } = new(Kind.PageViewsPerSession, "pageviews_per_session");

  /// <summary>Sessions that started with a page view of the page.</summary>
  public static Metric Entrances { get; } = new(Kind.Entrances, "entrances");

  /// <summary>Sessions whose last page view was the page.</summary>
  public static Metric Exits { get; } = new(Kind.Exits, "exits");

  /// <summary>Exits per page view, 0 to 1.</summary>
  public static Metric ExitRate { get; } = new(Kind.ExitRate, "exit_rate");

  /// <summary>Number of events of a specific event name.</summary>
  public static Metric EventsOf(string eventName) =>
      new(Kind.Events, "events:" + RequireName(eventName), eventName: eventName);

  /// <summary>Unique visitors of a specific event name.</summary>
  public static Metric VisitorsOf(string eventName) =>
      new(Kind.Visitors, "visitors:" + RequireName(eventName), eventName: eventName);

  /// <summary>Sum of a measured value.</summary>
  /// <param name="metric">The metric name, as tracked in <see cref="AnalyticsEvent.Metrics"/>.</param>
  /// <param name="eventName">The event; null means the query's base event.</param>
  public static Metric Sum(string metric, string eventName = null) => Value(Kind.Sum, "sum", metric, eventName);

  /// <summary>Average of a measured value.</summary>
  public static Metric Average(string metric, string eventName = null) => Value(Kind.Average, "avg", metric, eventName);

  /// <summary>Smallest measured value.</summary>
  public static Metric Min(string metric, string eventName = null) => Value(Kind.Min, "min", metric, eventName);

  /// <summary>Largest measured value.</summary>
  public static Metric Max(string metric, string eventName = null) => Value(Kind.Max, "max", metric, eventName);

  /// <summary>Number of measured values.</summary>
  public static Metric Count(string metric, string eventName = null) => Value(Kind.Count, "count", metric, eventName);

  /// <summary>Unique visitors who completed a goal event.</summary>
  public static Metric Conversions(string goalEvent) =>
      new(Kind.Conversions, "conversions:" + RequireName(goalEvent), argument: goalEvent);

  /// <summary>
  /// Unique visitors who completed a goal event divided by unique visitors of the base event,
  /// 0 to 1.
  /// </summary>
  public static Metric ConversionRate(string goalEvent) =>
      new(Kind.ConversionRate, "conversion_rate:" + RequireName(goalEvent), argument: goalEvent);

  /// <summary>
  /// A row's share of the total of another metric, 0 to 1, for example each source's share of
  /// visitors.
  /// </summary>
  public static Metric PercentOfTotal(Metric metric)
  {
    ArgumentNullException.ThrowIfNull(metric);
    if (metric.MetricKind == Kind.PercentOfTotal)
      throw new ArgumentException("PercentOfTotal cannot be nested.", nameof(metric));
    return new(Kind.PercentOfTotal, "percent:" + metric.Key, inner: metric);
  }

  static Metric Value(Kind kind, string prefix, string metric, string eventName)
  {
    RequireName(metric);
    var key = prefix + ":" + metric + (eventName == null ? "" : "@" + RequireName(eventName));
    return new(kind, key, argument: metric, eventName: eventName);
  }

  static string RequireName(string name)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (name.IndexOfAny([':', '@', ',']) >= 0)
      throw new ArgumentException($"'{name}' contains a reserved character (':', '@' or ',').");
    return name;
  }

  /// <summary>
  /// Parses a metric key, for example "visitors", "bounce_rate", "sum:revenue",
  /// "avg:duration_ms@job_completed", "conversion_rate:signup" or "percent:visitors".
  /// </summary>
  public static Metric Parse(string key)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(key);
    key = key.Trim();
    switch (key)
    {
      case "events": return Events;
      case "visitors": return Visitors;
      case "pageviews": return PageViews;
      case "sessions": return Sessions;
      case "bounces": return Bounces;
      case "bounce_rate": return BounceRate;
      case "session_duration": return SessionDuration;
      case "avg_session_duration": return AverageSessionDuration;
      case "pageviews_per_session": return PageViewsPerSession;
      case "entrances": return Entrances;
      case "exits": return Exits;
      case "exit_rate": return ExitRate;
    }
    var colon = key.IndexOf(':');
    if (colon <= 0)
      throw new FormatException($"Unknown metric '{key}'.");
    var prefix = key[..colon];
    var rest = key[(colon + 1)..];
    if (prefix == "percent")
      return PercentOfTotal(Parse(rest));
    var at = rest.IndexOf('@');
    var name = at < 0 ? rest : rest[..at];
    var eventName = at < 0 ? null : rest[(at + 1)..];
    return prefix switch
    {
      "events" => EventsOf(name),
      "visitors" => VisitorsOf(name),
      "sum" => Sum(name, eventName),
      "avg" => Average(name, eventName),
      "min" => Min(name, eventName),
      "max" => Max(name, eventName),
      "count" => Count(name, eventName),
      "conversions" => Conversions(name),
      "conversion_rate" => ConversionRate(name),
      _ => throw new FormatException($"Unknown metric '{key}'.")
    };
  }

  /// <summary>
  /// The events this metric reads.
  /// </summary>
  internal IEnumerable<string> Sources(string baseEvent)
  {
    switch (MetricKind)
    {
      case Kind.Events or Kind.Visitors or Kind.Sum or Kind.Average or Kind.Min or Kind.Max or Kind.Count:
        yield return EventName ?? baseEvent;
        break;
      case Kind.Sessions or Kind.Bounces or Kind.BounceRate or Kind.SessionDuration or
          Kind.AverageSessionDuration or Kind.PageViewsPerSession:
        yield return StandardEvents.Session;
        break;
      case Kind.Entrances or Kind.Exits or Kind.ExitRate:
        yield return StandardEvents.PageView;
        break;
      case Kind.Conversions:
        yield return Argument;
        break;
      case Kind.ConversionRate:
        yield return Argument;
        yield return baseEvent;
        break;
      case Kind.PercentOfTotal:
        foreach (var source in Inner.Sources(baseEvent))
          yield return source;
        break;
    }
  }

  /// <summary>
  /// The widest set of dimensions this metric's rows carry.
  /// </summary>
  internal DimensionScope Scope => MetricKind switch
  {
    Kind.Sessions or Kind.Bounces or Kind.BounceRate or Kind.SessionDuration or
        Kind.AverageSessionDuration or Kind.PageViewsPerSession => DimensionScope.Session,
    Kind.Entrances or Kind.Exits or Kind.ExitRate => DimensionScope.PageView,
    Kind.PercentOfTotal => Inner.Scope,
    _ => DimensionScope.All
  };

  /// <summary>
  /// Computes the metric from per-event aggregates. A missing aggregate counts as empty.
  /// </summary>
  internal double Evaluate(Func<string, RollupAccumulator> source, string baseEvent)
  {
    switch (MetricKind)
    {
      case Kind.Events:
        return source(EventName ?? baseEvent)?.Count ?? 0;
      case Kind.Visitors:
        return VisitorsOfSource(source(EventName ?? baseEvent));
      case Kind.Sessions:
        return source(StandardEvents.Session)?.Count ?? 0;
      case Kind.Bounces:
        return SumOf(source(StandardEvents.Session), StandardMetrics.Bounces);
      case Kind.BounceRate:
        return Ratio(SumOf(source(StandardEvents.Session), StandardMetrics.Bounces), source(StandardEvents.Session)?.Count ?? 0, clamp: true);
      case Kind.SessionDuration:
        return Math.Max(0, SumOf(source(StandardEvents.Session), StandardMetrics.Duration));
      case Kind.AverageSessionDuration:
        return Math.Max(0, Ratio(SumOf(source(StandardEvents.Session), StandardMetrics.Duration), source(StandardEvents.Session)?.Count ?? 0));
      case Kind.PageViewsPerSession:
        return Ratio(SumOf(source(StandardEvents.Session), StandardMetrics.SessionPageViews), source(StandardEvents.Session)?.Count ?? 0);
      case Kind.Entrances:
        return Math.Max(0, SumOf(source(StandardEvents.PageView), StandardMetrics.Entrances));
      case Kind.Exits:
        return Math.Max(0, SumOf(source(StandardEvents.PageView), StandardMetrics.Exits));
      case Kind.ExitRate:
        {
          var pageViews = source(StandardEvents.PageView);
          return Ratio(Math.Max(0, SumOf(pageViews, StandardMetrics.Exits)), pageViews?.Count ?? 0, clamp: true);
        }
      case Kind.Sum:
        return Value(source(EventName ?? baseEvent)).Sum;
      case Kind.Average:
        return Value(source(EventName ?? baseEvent)).Average;
      case Kind.Min:
        {
          var value = Value(source(EventName ?? baseEvent));
          return value.Count == 0 ? 0 : value.Min;
        }
      case Kind.Max:
        {
          var value = Value(source(EventName ?? baseEvent));
          return value.Count == 0 ? 0 : value.Max;
        }
      case Kind.Count:
        return Value(source(EventName ?? baseEvent)).Count;
      case Kind.Conversions:
        return VisitorsOfSource(source(Argument));
      case Kind.ConversionRate:
        return Ratio(VisitorsOfSource(source(Argument)), VisitorsOfSource(source(baseEvent)), clamp: true);
      default:
        throw new InvalidOperationException("PercentOfTotal is evaluated against totals.");
    }
  }

  static double VisitorsOfSource(RollupAccumulator accumulator) =>
      accumulator?.Visitors?.Estimate() ?? 0;

  static double SumOf(RollupAccumulator accumulator, string metric) =>
      accumulator != null && accumulator.Metrics.TryGetValue(metric, out var value) ? value.Sum : 0;

  MetricValue Value(RollupAccumulator accumulator) =>
      accumulator != null && accumulator.Metrics.TryGetValue(Argument, out var value) ? value : default;

  static double Ratio(double numerator, double denominator, bool clamp = false)
  {
    if (denominator <= 0)
      return 0;
    var ratio = numerator / denominator;
    return clamp ? Math.Clamp(ratio, 0, 1) : ratio;
  }

  /// <inheritdoc/>
  public bool Equals(Metric other) => other != null && Key == other.Key;

  /// <inheritdoc/>
  public override bool Equals(object obj) => obj is Metric other && Equals(other);

  /// <inheritdoc/>
  public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

  /// <inheritdoc/>
  public override string ToString() => Key;

  internal static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
