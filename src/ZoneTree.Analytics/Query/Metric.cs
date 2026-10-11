using System.Globalization;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics;

/// <summary>
/// A value a query computes for every row.
/// </summary>
/// <remarks>
/// <para>
/// Aggregates read one or more events. <see cref="Events"/>, <see cref="Visitors"/> and value
/// aggregates such as <see cref="Sum(string, string)"/> read the query's base event unless an
/// event is given. Session metrics read sessions, <see cref="PageViews"/>, <see cref="Entrances"/>
/// and <see cref="Exits"/> read page views, and goal metrics read their goal event. Rates are
/// fractions between 0 and 1; durations are seconds.
/// </para>
/// <para>
/// Metrics combine into formulas with <c>+</c>, <c>-</c>, <c>*</c> and <c>/</c>, for example
/// <c>Metric.Sum("revenue") / Metric.EventsOf("purchase")</c>; division by zero, and a result that
/// overflows, gives zero.
/// Series functions such as <see cref="Cumulative"/>, <see cref="MovingAverage"/> and
/// <see cref="Rate"/> transform a metric along the time series of each group. Totals apply a
/// formula to the totals of its parts, not to the sum of its rows.
/// </para>
/// <para>
/// Session metrics can only be grouped and filtered by session-level dimensions (standard ones
/// and <see cref="SessionOptions.Dimensions"/>) and the entry page; entrance and exit metrics
/// additionally by page and hostname. Use a <see cref="Segment"/> to select sessions by what
/// happened in them. <see cref="BotHits"/> can only be grouped and filtered by the dimensions of
/// bot events.
/// </para>
/// <para>
/// Every metric has a stable <see cref="Key"/>, which <see cref="Parse"/> accepts, for example
/// "visitors", "bounce_rate", "sum:revenue", "p95:load_time", "conversion_rate:signup",
/// "div(sum:revenue,events:purchase)" or "moving_avg(visitors,7)".
/// </para>
/// </remarks>
public sealed class Metric : IEquatable<Metric>
{
  static readonly char[] ReservedCharacters = [':', '@', ',', '(', ')'];

  internal enum Kind
  {
    // Aggregates: read from the aggregated rows of a group.
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
    StandardDeviation,
    Variance,
    Percentile,
    First,
    Last,
    Distinct,
    CountBetween,
    ConversionRate,

    // Formulas: computed from other metrics of the same row.
    Constant,
    Add,
    Subtract,
    Multiply,
    Divide,
    PercentOfTotal,

    // Series functions: computed along the time series of a group.
    Rate,
    Cumulative,
    MovingAverage,
    Difference,
    Increase,
    Lag,
    Lead,
  }

  Metric(
      Kind kind,
      string key,
      string argument = null,
      string eventName = null,
      double parameter = 0,
      Metric[] operands = null,
      double upperBound = 0,
      Filter[] conditions = null,
      string unconditionedKey = null)
  {
    MetricKind = kind;
    Key = key;
    Argument = argument;
    EventName = eventName;
    Parameter = parameter;
    Operands = operands ?? [];
    UpperBound = upperBound;
    Conditions = conditions ?? [];
    UnconditionedKey = unconditionedKey ?? key;
    conditionSuffix = Conditions.Length == 0 ? "" : ConditionSeparator + string.Join(ConditionSeparator, Conditions.Select(x => x.ToString()));
  }

  const string ConditionSeparator = "\u001f";

  readonly string conditionSuffix;

  sealed record SourceKeyCache(string Event, string Key);

  SourceKeyCache sourceKeyCache;

  internal Kind MetricKind { get; }

  /// <summary>
  /// The value name for value aggregates, the dimension for <see cref="Distinct"/>, or the goal
  /// event for goal metrics.
  /// </summary>
  internal string Argument { get; }

  /// <summary>An explicit event for count, visitor and value aggregates; null means the base event.</summary>
  internal string EventName { get; }

  /// <summary>
  /// The percentile (0 to 100), the constant, the window or shift in buckets, the rate unit in
  /// seconds, or the lower bound of <see cref="CountBetween"/>.
  /// </summary>
  internal double Parameter { get; }

  /// <summary>The upper bound of <see cref="CountBetween"/>.</summary>
  internal double UpperBound { get; }

  /// <summary>The operands of formulas and series functions.</summary>
  internal Metric[] Operands { get; }

  /// <summary>
  /// The filters of a conditional aggregate (see <see cref="Where"/>), in key order; empty otherwise.
  /// </summary>
  internal Filter[] Conditions { get; }

  /// <summary>The key of an aggregate without its conditions.</summary>
  internal string UnconditionedKey { get; }

  /// <summary>
  /// The stable identifier of the metric.
  /// </summary>
  public string Key { get; }

  #region Aggregates

  /// <summary>Number of base events. For the session event, the number of sessions.</summary>
  public static Metric Events { get; } = new(Kind.Events, "events");

  /// <summary>Unique visitors of the base event.</summary>
  public static Metric Visitors { get; } = new(Kind.Visitors, "visitors");

  /// <summary>Number of page views.</summary>
  public static Metric PageViews { get; } = new(Kind.Events, "pageviews", eventName: StandardEvents.PageView);

  /// <summary>
  /// Number of bot hits (<see cref="StandardEvents.Bot"/> events), recorded when
  /// <see cref="WebOptions.Bots"/> is <see cref="BotTraffic.TrackSeparately"/>. Group it by page or
  /// hostname to compare with page views.
  /// </summary>
  public static Metric BotHits { get; } = new(Kind.Events, "bot_hits", eventName: StandardEvents.Bot);

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

  /// <summary>Sum of a tracked value.</summary>
  /// <remarks>A sum outside the finite double range is infinite.</remarks>
  /// <param name="value">The value name, as tracked in <see cref="AnalyticsEvent.Values"/>.</param>
  /// <param name="eventName">The event; null means the query's base event.</param>
  public static Metric Sum(string value, string eventName = null) => Value(Kind.Sum, "sum", value, eventName);

  /// <summary>Average of a tracked value.</summary>
  public static Metric Average(string value, string eventName = null) => Value(Kind.Average, "avg", value, eventName);

  /// <summary>Smallest measured value.</summary>
  public static Metric Min(string value, string eventName = null) => Value(Kind.Min, "min", value, eventName);

  /// <summary>Largest measured value.</summary>
  public static Metric Max(string value, string eventName = null) => Value(Kind.Max, "max", value, eventName);

  /// <summary>Number of tracked values.</summary>
  public static Metric Count(string value, string eventName = null) => Value(Kind.Count, "count", value, eventName);

  /// <summary>Population standard deviation of a tracked value.</summary>
  public static Metric StandardDeviation(string value, string eventName = null) =>
      Value(Kind.StandardDeviation, "stddev", value, eventName);

  /// <summary>Population variance of a tracked value.</summary>
  /// <remarks>A variance outside the finite double range is infinite; the standard deviation may still be finite.</remarks>
  public static Metric Variance(string value, string eventName = null) => Value(Kind.Variance, "variance", value, eventName);

  /// <summary>
  /// A percentile of a tracked value, for example the 95th percentile of a load time, within a
  /// relative error of 1%. Percentile 0 is the minimum and 100 the maximum, exactly.
  /// </summary>
  /// <remarks>
  /// Rollups answer it when <see cref="RollupOptions.ValueAggregates"/> includes
  /// <see cref="ValueAggregates.Percentiles"/> for the value; otherwise raw events do.
  /// Magnitudes below 1e-9 share a zero bucket. Each sign keeps at most 2,048 logarithmic
  /// buckets (about 17 orders of magnitude); beyond that span, smaller magnitudes collapse
  /// into the lowest retained bucket. The relative-error guarantee excludes those buckets.
  /// </remarks>
  /// <param name="value">The value name, as tracked in <see cref="AnalyticsEvent.Values"/>.</param>
  /// <param name="percentile">The percentile, 0 to 100.</param>
  /// <param name="eventName">The event; null means the query's base event.</param>
  public static Metric Percentile(string value, double percentile, string eventName = null)
  {
    if (!(percentile >= 0 && percentile <= 100))
      throw new ArgumentOutOfRangeException(nameof(percentile), "The percentile must be between 0 and 100.");
    RequireName(value);
    var prefix = "p" + percentile.ToString("R", CultureInfo.InvariantCulture);
    return new(Kind.Percentile, prefix + ":" + value + EventSuffix(eventName), value, eventName, percentile);
  }

  /// <summary>The median (50th percentile) of a tracked value. See <see cref="Percentile"/>.</summary>
  public static Metric Median(string value, string eventName = null) => Percentile(value, 50, eventName);

  /// <summary>
  /// The first tracked value of each row, by time: the opening value of a gauge.
  /// </summary>
  /// <remarks>
  /// Rollups answer it when <see cref="RollupOptions.ValueAggregates"/> includes
  /// <see cref="ValueAggregates.FirstAndLast"/> for the value; otherwise raw events do. Values
  /// at the same instant are ordered by value.
  /// </remarks>
  public static Metric First(string value, string eventName = null) => Value(Kind.First, "first", value, eventName);

  /// <summary>
  /// The last tracked value of each row, by time: the current value of a gauge, such as a
  /// temperature or a queue length.
  /// </summary>
  /// <remarks>See <see cref="First"/>.</remarks>
  public static Metric Last(string value, string eventName = null) => Value(Kind.Last, "last", value, eventName);

  /// <summary>
  /// The number of distinct values of a dimension, for example distinct countries or tenants.
  /// Exact up to 2^precision / 4 values, then estimated like <see cref="Visitors"/>.
  /// </summary>
  /// <remarks>
  /// Rollups answer it when <see cref="RollupOptions.DistinctDimensions"/> includes the
  /// dimension; otherwise raw events do.
  /// On <see cref="StandardEvents.Session"/>, the dimension must be session-level or the entry page.
  /// </remarks>
  /// <param name="dimension">The dimension.</param>
  /// <param name="eventName">The event; null means the query's base event.</param>
  public static Metric Distinct(string dimension, string eventName = null) =>
      Value(Kind.Distinct, "distinct", dimension, eventName);

  /// <summary>
  /// The number of tracked values from <paramref name="lower"/> (inclusive) to
  /// <paramref name="upper"/> (exclusive), for example page loads under 100 ms. Bounds may be
  /// infinite. Values are placed within 1% of their magnitude, like <see cref="Percentile"/>, so a
  /// value within 1% of a bound may count on either side of it.
  /// </summary>
  /// <remarks>
  /// Rollups answer it when <see cref="RollupOptions.ValueAggregates"/> includes
  /// <see cref="ValueAggregates.Percentiles"/> for the value; otherwise raw events do.
  /// </remarks>
  public static Metric CountBetween(string value, double lower, double upper, string eventName = null)
  {
    RequireName(value);
    if (double.IsNaN(lower) || double.IsNaN(upper) || !(lower < upper))
      throw new ArgumentOutOfRangeException(nameof(upper), "The upper bound must be greater than the lower bound.");
    var key = "count_between(" + value + EventSuffix(eventName) + "," + Format(lower) + "," + Format(upper) + ")";
    return new(Kind.CountBetween, key, value, eventName, lower, upperBound: upper);
  }

  /// <summary>
  /// A histogram of a tracked value: one <see cref="CountBetween"/> metric per bin between
  /// consecutive edges, for example <c>[0, 100, 250, 500, 1000, double.PositiveInfinity]</c>.
  /// </summary>
  /// <param name="value">The value name, as tracked in <see cref="AnalyticsEvent.Values"/>.</param>
  /// <param name="edges">The bin edges, in increasing order; at least two.</param>
  /// <param name="eventName">The event; null means the query's base event.</param>
  public static Metric[] Histogram(string value, IReadOnlyList<double> edges, string eventName = null)
  {
    ArgumentNullException.ThrowIfNull(edges);
    if (edges.Count < 2)
      throw new ArgumentException("A histogram needs at least two edges.", nameof(edges));
    var bins = new Metric[edges.Count - 1];
    for (var i = 0; i < bins.Length; ++i)
      bins[i] = CountBetween(value, edges[i], edges[i + 1], eventName);
    return bins;
  }

  /// <summary>
  /// Unique visitors who completed a goal event divided by unique visitors of the base event,
  /// 0 to 1. The number of converted visitors is <see cref="VisitorsOf(string)"/> of the goal event.
  /// </summary>
  public static Metric ConversionRate(string goalEvent) =>
      new(Kind.ConversionRate, "conversion_rate:" + RequireName(goalEvent), argument: goalEvent);

  /// <summary>
  /// This metric computed only from the events that match the filters, in addition to the
  /// query's filters: conditional aggregates such as CountIf and SumIf. For example
  /// <c>Metric.Events.Where(Filter.GreaterThanOrEqual("status", 500))</c> counts errors next to
  /// all requests in one query. On a formula, the filters apply to every aggregate in it.
  /// </summary>
  /// <remarks>
  /// Each event and set of filters is a sub-query of its own, joined with the others on the time
  /// bucket and group. Rollups answer it when a rollup definition covers the query's group-by and
  /// filter dimensions together with the dimensions of these filters; otherwise raw events do.
  /// </remarks>
  public Metric Where(params Filter[] filters)
  {
    ArgumentNullException.ThrowIfNull(filters);
    if (filters.Any(x => x == null))
      throw new ArgumentException("Filters cannot be null.", nameof(filters));
    return filters.Length == 0 ? this : WithConditions(filters);
  }

  Metric WithConditions(Filter[] filters)
  {
    if (MetricKind == Kind.Constant)
      return this;
    if (!IsAggregate)
      return Rebuild([.. Operands.Select(x => x.WithConditions(filters))]);
    var conditions = Conditions.Concat(filters)
        .DistinctBy(x => x.ToString())
        .OrderBy(x => x.ToString(), StringComparer.Ordinal)
        .ToArray();
    var key = "where(" + UnconditionedKey + "," + string.Join(",", conditions.Select(x => Quote(x.ToString()))) + ")";
    return new(MetricKind, key, Argument, EventName, Parameter, null, UpperBound, conditions, UnconditionedKey);
  }

  /// <summary>
  /// A formula or series function of the same kind and parameters over other operands.
  /// </summary>
  Metric Rebuild(Metric[] operands) => MetricKind switch
  {
    Kind.Add => Add(operands[0], operands[1]),
    Kind.Subtract => Subtract(operands[0], operands[1]),
    Kind.Multiply => Multiply(operands[0], operands[1]),
    Kind.Divide => Divide(operands[0], operands[1]),
    Kind.PercentOfTotal => PercentOfTotal(operands[0]),
    Kind.Rate => RateSeconds(operands[0], Parameter),
    Kind.Cumulative => Cumulative(operands[0]),
    Kind.MovingAverage => MovingAverage(operands[0], (int)Parameter),
    Kind.Difference => Difference(operands[0]),
    Kind.Increase => Increase(operands[0]),
    Kind.Lag => Lag(operands[0], (int)Parameter),
    Kind.Lead => Lead(operands[0], (int)Parameter),
    _ => throw new InvalidOperationException($"'{Key}' has no operands.")
  };

  /// <summary>
  /// A filter expression in a key: double quotes, with backslash escapes for quotes and backslashes.
  /// </summary>
  static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

  static string Unquote(string text)
  {
    if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
      throw new FormatException($"Expected a quoted filter, not {text}.");
    var result = new System.Text.StringBuilder(text.Length);
    for (var i = 1; i < text.Length - 1; ++i)
    {
      if (text[i] == '\\' && i + 1 < text.Length - 1)
        ++i;
      result.Append(text[i]);
    }
    return result.ToString();
  }

  static Metric Value(Kind kind, string prefix, string value, string eventName) =>
      new(kind, prefix + ":" + RequireName(value) + EventSuffix(eventName), value, eventName);

  static string EventSuffix(string eventName) => eventName == null ? "" : "@" + RequireName(eventName);

  static string RequireName(string name)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    if (name.IndexOfAny(ReservedCharacters) >= 0)
      throw new ArgumentException($"'{name}' contains a reserved character (':', '@', ',', '(' or ')').");
    return name;
  }

  #endregion

  #region Formulas

  /// <summary>A constant, for formulas such as <c>Metric.Sum("bytes") * 8</c>.</summary>
  public static Metric Constant(double value)
  {
    if (!double.IsFinite(value))
      throw new ArgumentOutOfRangeException(nameof(value), "A constant must be a finite number.");
    return new(Kind.Constant, Format(value), parameter: value);
  }

  /// <summary>The sum of two metrics.</summary>
  public static Metric Add(Metric left, Metric right) => Binary(Kind.Add, "add", left, right);

  /// <summary>The difference of two metrics.</summary>
  public static Metric Subtract(Metric left, Metric right) => Binary(Kind.Subtract, "sub", left, right);

  /// <summary>The product of two metrics.</summary>
  public static Metric Multiply(Metric left, Metric right) => Binary(Kind.Multiply, "mul", left, right);

  /// <summary>The quotient of two metrics; zero when the divisor is zero.</summary>
  public static Metric Divide(Metric left, Metric right) => Binary(Kind.Divide, "div", left, right);

  /// <summary>The sum of two metrics.</summary>
  public static Metric operator +(Metric left, Metric right) => Add(left, right);

  /// <summary>The difference of two metrics.</summary>
  public static Metric operator -(Metric left, Metric right) => Subtract(left, right);

  /// <summary>The product of two metrics.</summary>
  public static Metric operator *(Metric left, Metric right) => Multiply(left, right);

  /// <summary>The quotient of two metrics; zero when the divisor is zero.</summary>
  public static Metric operator /(Metric left, Metric right) => Divide(left, right);

  /// <summary>A metric plus a constant.</summary>
  public static Metric operator +(Metric left, double right) => Add(left, Constant(right));

  /// <summary>A metric minus a constant.</summary>
  public static Metric operator -(Metric left, double right) => Subtract(left, Constant(right));

  /// <summary>A metric times a constant.</summary>
  public static Metric operator *(Metric left, double right) => Multiply(left, Constant(right));

  /// <summary>A metric divided by a constant.</summary>
  public static Metric operator /(Metric left, double right) => Divide(left, Constant(right));

  /// <summary>A constant plus a metric.</summary>
  public static Metric operator +(double left, Metric right) => Add(Constant(left), right);

  /// <summary>A constant minus a metric.</summary>
  public static Metric operator -(double left, Metric right) => Subtract(Constant(left), right);

  /// <summary>A constant times a metric.</summary>
  public static Metric operator *(double left, Metric right) => Multiply(Constant(left), right);

  /// <summary>A constant divided by a metric; zero when the metric is zero.</summary>
  public static Metric operator /(double left, Metric right) => Divide(Constant(left), right);

  /// <summary>
  /// A row's share of the total of another metric, for example each source's share of
  /// visitors. The totals row is 1, or 0 when the total is 0.
  /// </summary>
  public static Metric PercentOfTotal(Metric metric) => Unary(Kind.PercentOfTotal, "percent", metric);

  static Metric Binary(Kind kind, string name, Metric left, Metric right)
  {
    ArgumentNullException.ThrowIfNull(left);
    ArgumentNullException.ThrowIfNull(right);
    return new(kind, name + "(" + left.Key + "," + right.Key + ")", operands: [left, right]);
  }

  static Metric Unary(Kind kind, string name, Metric metric, double parameter = 0, string parameterText = null)
  {
    ArgumentNullException.ThrowIfNull(metric);
    var key = name + "(" + metric.Key + (parameterText == null ? "" : "," + parameterText) + ")";
    return new(kind, key, parameter: parameter, operands: [metric]);
  }

  static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

  #endregion

  #region Series functions

  /// <summary>
  /// A metric per unit of time: per second by default, or per <paramref name="per"/>, for
  /// example requests per minute. Each row divides by the length of its bucket within the
  /// query range, not counting the future; the totals row gives the average rate over the range.
  /// </summary>
  public static Metric Rate(Metric metric, TimeSpan? per = null) =>
      RateSeconds(metric, (per ?? TimeSpan.FromSeconds(1)).TotalSeconds);

  static Metric RateSeconds(Metric metric, double seconds)
  {
    if (!(seconds > 0) || double.IsInfinity(seconds))
      throw new ArgumentOutOfRangeException(nameof(seconds), "The rate unit must be positive.");
    return Unary(Kind.Rate, "rate", metric, seconds, seconds == 1 ? null : Format(seconds));
  }

  /// <summary>
  /// The running total of a metric along the time series of each group, for example
  /// month-to-date revenue. The totals row holds the metric's total. Requires an interval.
  /// </summary>
  public static Metric Cumulative(Metric metric) => Unary(Kind.Cumulative, "cumulative", metric);

  /// <summary>
  /// The average of a metric over the current and previous buckets of each group, for example a
  /// 7-day moving average of visitors. The first buckets average the buckets available. The
  /// totals row holds the metric's total. Requires an interval.
  /// </summary>
  /// <remarks>
  /// The window counts interval positions. With <see cref="FillMode.None"/>, omitted buckets
  /// contribute zero; initial windows are clipped to the beginning of the query range.
  /// </remarks>
  /// <param name="metric">The metric.</param>
  /// <param name="buckets">The window, in buckets including the current one: 2 or more.</param>
  public static Metric MovingAverage(Metric metric, int buckets)
  {
    if (buckets < 2 || buckets > 10_000)
      throw new ArgumentOutOfRangeException(nameof(buckets), "The window must be between 2 and 10,000 buckets.");
    return Unary(Kind.MovingAverage, "moving_avg", metric, buckets, buckets.ToString(CultureInfo.InvariantCulture));
  }

  /// <summary>
  /// The change of a metric from the previous bucket of each group; zero for the first bucket.
  /// The totals row holds the sum of the changes. Requires an interval.
  /// </summary>
  public static Metric Difference(Metric metric) => Unary(Kind.Difference, "diff", metric);

  /// <summary>
  /// The increase of a counter from the previous bucket of each group, such as an energy meter
  /// read with <see cref="Last"/>. A decrease is a counter reset: the increase is then the
  /// current value. Zero for the first bucket. The totals row holds the sum of the increases.
  /// Requires an interval.
  /// </summary>
  public static Metric Increase(Metric metric) => Unary(Kind.Increase, "increase", metric);

  /// <summary>
  /// The value of a metric <paramref name="buckets"/> buckets earlier in the same group, for
  /// comparisons with earlier buckets such as week-over-week growth:
  /// <c>Metric.Visitors / Metric.Lag(Metric.Visitors, 7) - 1</c>. Zero when there is no such
  /// bucket. The totals row holds the metric's total. Requires an interval.
  /// </summary>
  /// <remarks>Counts interval positions even with <see cref="FillMode.None"/>; omitted target buckets give zero.</remarks>
  public static Metric Lag(Metric metric, int buckets = 1) => Shift(Kind.Lag, "lag", metric, buckets);

  /// <summary>
  /// The value of a metric <paramref name="buckets"/> buckets later in the same group. Zero when
  /// there is no such bucket. The totals row holds the metric's total. Requires an interval.
  /// </summary>
  /// <remarks>Counts interval positions even with <see cref="FillMode.None"/>; omitted target buckets give zero.</remarks>
  public static Metric Lead(Metric metric, int buckets = 1) => Shift(Kind.Lead, "lead", metric, buckets);

  static Metric Shift(Kind kind, string name, Metric metric, int buckets)
  {
    if (buckets < 1 || buckets > 10_000)
      throw new ArgumentOutOfRangeException(nameof(buckets), "The shift must be between 1 and 10,000 buckets.");
    return Unary(kind, name, metric, buckets, buckets.ToString(CultureInfo.InvariantCulture));
  }

  #endregion

  #region Parsing

  /// <summary>
  /// Parses a metric key: an aggregate such as "visitors", "bounce_rate", "sum:revenue",
  /// "avg:duration_ms@job_completed", "p95:load_time", "distinct:country" or
  /// "conversion_rate:signup"; a formula such as "div(sum:revenue,events:purchase)" or
  /// "mul(sum:bytes,8)" (functions add, sub, mul, div and percent); or a series function such as
  /// "rate(events)", "rate(events,60)", "cumulative(sum:revenue)", "moving_avg(visitors,7)",
  /// "diff(last:temperature)", "increase(last:energy)", "lag(visitors,7)" or "lead(visitors,1)";
  /// a histogram bin such as "count_between(load_time,100,500)"; or a conditional metric such as
  /// <c>where(events,"status&gt;=500")</c>, with filter expressions (see <see cref="Filter.Parse"/>)
  /// in double quotes and backslash escapes for quotes and backslashes.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
  /// <exception cref="FormatException">The key is not a metric.</exception>
  public static Metric Parse(string key) => Parsing.Parse(key, "metric", ParseExpression);

  /// <summary>
  /// Tries to parse a metric key. See <see cref="Parse"/>.
  /// </summary>
  public static bool TryParse(string key, out Metric metric) => Parsing.TryParse(key, Parse, out metric);

  static Metric ParseExpression(string text)
  {
    var open = text.IndexOf('(');
    if (open > 0 && text.EndsWith(')'))
    {
      var name = text[..open];
      var arguments = SplitArguments(text[(open + 1)..^1]);
      return (name, arguments.Count) switch
      {
        ("add", 2) => Add(ParseExpression(arguments[0]), ParseExpression(arguments[1])),
        ("sub", 2) => Subtract(ParseExpression(arguments[0]), ParseExpression(arguments[1])),
        ("mul", 2) => Multiply(ParseExpression(arguments[0]), ParseExpression(arguments[1])),
        ("div", 2) => Divide(ParseExpression(arguments[0]), ParseExpression(arguments[1])),
        ("percent", 1) => PercentOfTotal(ParseExpression(arguments[0])),
        ("rate", 1) => Rate(ParseExpression(arguments[0])),
        ("rate", 2) => RateSeconds(ParseExpression(arguments[0]), ParseNumber(arguments[1])),
        ("cumulative", 1) => Cumulative(ParseExpression(arguments[0])),
        ("moving_avg", 2) => MovingAverage(ParseExpression(arguments[0]), ParseWindow(arguments[1])),
        ("diff", 1) => Difference(ParseExpression(arguments[0])),
        ("increase", 1) => Increase(ParseExpression(arguments[0])),
        ("lag", 1) => Lag(ParseExpression(arguments[0])),
        ("lag", 2) => Lag(ParseExpression(arguments[0]), ParseWindow(arguments[1])),
        ("lead", 1) => Lead(ParseExpression(arguments[0])),
        ("lead", 2) => Lead(ParseExpression(arguments[0]), ParseWindow(arguments[1])),
        ("count_between", 3) => ParseCountBetween(arguments),
        ("where", >= 2) => ParseExpression(arguments[0]).Where([.. arguments.Skip(1).Select(x => Filter.Parse(Unquote(x)))]),
        _ => throw new FormatException($"Unknown metric function '{name}' with {arguments.Count} argument(s).")
      };
    }
    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var constant))
      return Constant(constant);
    return ParseAggregate(text);
  }

  static Metric ParseCountBetween(List<string> arguments)
  {
    var at = arguments[0].IndexOf('@');
    var metric = at < 0 ? arguments[0] : arguments[0][..at];
    var eventName = at < 0 ? null : arguments[0][(at + 1)..];
    return CountBetween(metric, ParseNumber(arguments[1]), ParseNumber(arguments[2]), eventName);
  }

  /// <summary>
  /// Splits function arguments at the commas outside nested parentheses and quoted filters.
  /// </summary>
  static List<string> SplitArguments(string text)
  {
    var arguments = new List<string>();
    var depth = 0;
    var start = 0;
    var quoted = false;
    for (var i = 0; i < text.Length; ++i)
    {
      if (quoted)
      {
        if (text[i] == '\\')
          ++i;
        else if (text[i] == '"')
          quoted = false;
        continue;
      }
      switch (text[i])
      {
        case '"':
          quoted = true;
          break;
        case '(':
          ++depth;
          break;
        case ')':
          if (--depth < 0)
            throw new FormatException("Unbalanced parentheses.");
          break;
        case ',' when depth == 0:
          arguments.Add(text[start..i].Trim());
          start = i + 1;
          break;
      }
    }
    if (depth != 0)
      throw new FormatException("Unbalanced parentheses.");
    arguments.Add(text[start..].Trim());
    return arguments;
  }

  static double ParseNumber(string text) =>
      double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
          ? value
          : throw new FormatException($"'{text}' is not a number.");

  static int ParseWindow(string text) =>
      int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
          ? value
          : throw new FormatException($"'{text}' is not a window size.");

  static Metric ParseAggregate(string key)
  {
    switch (key)
    {
      case "events": return Events;
      case "visitors": return Visitors;
      case "pageviews": return PageViews;
      case "bot_hits": return BotHits;
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
    var at = rest.IndexOf('@');
    var name = at < 0 ? rest : rest[..at];
    var eventName = at < 0 ? null : rest[(at + 1)..];
    if (prefix.Length > 1 && prefix[0] == 'p' &&
        double.TryParse(prefix.AsSpan(1), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percentile))
      return Percentile(name, percentile, eventName);
    return prefix switch
    {
      "events" => EventsOf(name),
      "visitors" => VisitorsOf(name),
      "sum" => Sum(name, eventName),
      "avg" => Average(name, eventName),
      "min" => Min(name, eventName),
      "max" => Max(name, eventName),
      "count" => Count(name, eventName),
      "stddev" => StandardDeviation(name, eventName),
      "variance" => Variance(name, eventName),
      "first" => First(name, eventName),
      "last" => Last(name, eventName),
      "distinct" => Distinct(name, eventName),
      "conversion_rate" => ConversionRate(name),
      _ => throw new FormatException($"Unknown metric '{key}'.")
    };
  }

  #endregion

  #region Analysis

  /// <summary>True for metrics read from aggregated rows.</summary>
  internal bool IsAggregate => MetricKind < Kind.Constant;

  /// <summary>
  /// True for series functions that need the previous buckets of a row's group, and therefore
  /// an interval.
  /// </summary>
  internal bool NeedsSeries =>
      MetricKind is Kind.Cumulative or Kind.MovingAverage or Kind.Difference or Kind.Increase or Kind.Lag or Kind.Lead ||
      Operands.Any(x => x.NeedsSeries);

  /// <summary>
  /// The aggregates this metric is computed from.
  /// </summary>
  internal IEnumerable<Metric> Aggregates()
  {
    if (IsAggregate)
    {
      yield return this;
      yield break;
    }
    foreach (var operand in Operands)
    {
      foreach (var aggregate in operand.Aggregates())
        yield return aggregate;
    }
  }

  /// <summary>
  /// Session metrics read <see cref="StandardEvents.Session"/> rows.
  /// </summary>
  bool IsSessionMetric => MetricKind is Kind.Sessions or Kind.Bounces or Kind.BounceRate or
      Kind.SessionDuration or Kind.AverageSessionDuration or Kind.PageViewsPerSession;

  /// <summary>
  /// Page flow metrics read the entrances and exits maintained on page view rows.
  /// </summary>
  bool IsPageFlowMetric => MetricKind is Kind.Entrances or Kind.Exits or Kind.ExitRate;

  /// <summary>
  /// True for an aggregate of entrances or exits.
  /// </summary>
  internal bool ReadsPageFlow => IsPageFlowMetric;

  /// <summary>
  /// Identifies the sub-query of an event and a set of conditions: the event name alone when
  /// there are no conditions.
  /// </summary>
  internal static string SourceKeyOf(string eventName, Filter[] conditions) =>
      conditions.Length == 0
          ? eventName
          : eventName + ConditionSeparator + string.Join(ConditionSeparator, conditions.Select(x => x.ToString()));

  /// <summary>
  /// The sub-query this aggregate reads for an event.
  /// </summary>
  internal string SourceKey(string eventName)
  {
    if (conditionSuffix.Length == 0)
      return eventName;
    // Rows are evaluated one after another with the same event: cache the last key.
    var cache = sourceKeyCache;
    if (cache != null && string.Equals(cache.Event, eventName, StringComparison.Ordinal))
      return cache.Key;
    var key = eventName + conditionSuffix;
    sourceKeyCache = new SourceKeyCache(eventName, key);
    return key;
  }

  /// <summary>
  /// The events an aggregate reads.
  /// </summary>
  internal IEnumerable<string> Sources(string baseEvent)
  {
    if (!IsAggregate)
      return Aggregates().SelectMany(x => x.Sources(baseEvent)).Distinct(StringComparer.Ordinal);
    if (IsSessionMetric)
      return [StandardEvents.Session];
    if (IsPageFlowMetric)
      return [StandardEvents.PageView];
    return MetricKind switch
    {
      Kind.ConversionRate => [Argument, baseEvent],
      _ => [EventName ?? baseEvent]
    };
  }

  /// <summary>
  /// The aggregates rollups must hold beyond the standard ones, for value aggregates of opt-in
  /// kinds: the event read and the metric aggregates required.
  /// </summary>
  internal ValueAggregates RequiredAggregates => MetricKind switch
  {
    Kind.Percentile or Kind.CountBetween => ValueAggregates.Percentiles,
    Kind.First or Kind.Last => ValueAggregates.FirstAndLast,
    _ => ValueAggregates.None
  };

  /// <summary>
  /// The widest set of dimensions the rows an aggregate reads carry, for a base event.
  /// </summary>
  internal DimensionScope ScopeFor(string baseEvent) =>
      Sources(baseEvent).Contains(StandardEvents.Bot) ? DimensionScope.Bot :
      Sources(baseEvent).Contains(StandardEvents.Session) ? DimensionScope.Session :
      IsPageFlowMetric ? DimensionScope.PageView :
      DimensionScope.All;

  #endregion

  #region Evaluation

  /// <summary>
  /// Computes an aggregate from its source; conversion rates also read the base source.
  /// A missing aggregate counts as empty.
  /// </summary>
  internal double EvaluateAggregate(RollupAccumulator source, RollupAccumulator baseSource = null)
  {
    if (IsSessionMetric)
      return EvaluateSession(source);
    if (IsPageFlowMetric)
      return EvaluatePageFlow(source);
    switch (MetricKind)
    {
      case Kind.Events:
        return source?.Count ?? 0;
      case Kind.Visitors:
        return VisitorsOf(source);
      case Kind.ConversionRate:
        return Ratio(VisitorsOf(source), VisitorsOf(baseSource), clamp: true);
      case Kind.Distinct:
        return source?.DistinctOf(Argument)?.Estimate() ?? 0;
    }
    var accumulator = source;
    var value = accumulator != null && accumulator.TryGetValueStats(Argument, out var found) ? found : default;
    return MetricKind switch
    {
      Kind.Sum => value.Sum,
      Kind.Average => value.Average,
      Kind.Min => value.Count == 0 ? 0 : value.Min,
      Kind.Max => value.Count == 0 ? 0 : value.Max,
      Kind.StandardDeviation => value.StandardDeviation,
      Kind.Variance => value.Variance,
      Kind.Percentile => PercentileOf(value, accumulator?.DetailOf(Argument)),
      Kind.First => accumulator?.DetailOf(Argument) is { HasFirstLast: true } first ? first.First : 0,
      Kind.Last => accumulator?.DetailOf(Argument) is { HasFirstLast: true } last ? last.Last : 0,
      Kind.CountBetween => CountBetweenOf(value, accumulator?.DetailOf(Argument)),
      _ => value.Count
    };
  }

  double CountBetweenOf(ValueStats value, ValueDetail detail)
  {
    if (value.Count == 0)
      return 0;
    // A range that holds every value counts them exactly.
    if (Parameter <= value.Min && UpperBound > value.Max)
      return value.Count;
    return detail?.Quantiles?.CountBetween(Parameter, UpperBound) ?? 0;
  }

  double PercentileOf(ValueStats value, ValueDetail detail)
  {
    if (value.Count == 0)
      return 0;
    // The extremes are known exactly; every other percentile is within the sketch accuracy.
    if (Parameter == 0)
      return value.Min;
    if (Parameter == 100)
      return value.Max;
    if (detail?.Quantiles == null)
      return 0;
    return Math.Clamp(detail.Quantiles.Quantile(Parameter / 100), value.Min, value.Max);
  }

  double EvaluateSession(RollupAccumulator sessions)
  {
    var count = sessions?.Count ?? 0;
    return MetricKind switch
    {
      Kind.Sessions => count,
      Kind.Bounces => SumOf(sessions, StandardValues.Bounces),
      Kind.BounceRate => Ratio(SumOf(sessions, StandardValues.Bounces), count, clamp: true),
      Kind.SessionDuration => Math.Max(0, SumOf(sessions, StandardValues.Duration)),
      Kind.AverageSessionDuration => Math.Max(0, Ratio(SumOf(sessions, StandardValues.Duration), count)),
      _ => Ratio(SumOf(sessions, StandardValues.SessionPageViews), count)
    };
  }

  double EvaluatePageFlow(RollupAccumulator pageViews)
  {
    // Entrances and exits are sums of deltas; never report a negative count.
    return MetricKind switch
    {
      Kind.Entrances => Math.Max(0, SumOf(pageViews, StandardValues.Entrances)),
      Kind.Exits => Math.Max(0, SumOf(pageViews, StandardValues.Exits)),
      _ => Ratio(Math.Max(0, SumOf(pageViews, StandardValues.Exits)), pageViews?.Count ?? 0, clamp: true)
    };
  }

  static double VisitorsOf(RollupAccumulator accumulator) =>
      accumulator?.Visitors?.Estimate() ?? 0;

  static double SumOf(RollupAccumulator accumulator, string metric) =>
      accumulator != null && accumulator.TryGetValueStats(metric, out var value) ? value.Sum : 0;

  static double Ratio(double numerator, double denominator, bool clamp = false)
  {
    if (denominator <= 0)
      return 0;
    var ratio = numerator / denominator;
    return clamp ? Math.Clamp(ratio, 0, 1) : ratio;
  }

  #endregion

  /// <inheritdoc/>
  public bool Equals(Metric other) => other is not null && Key == other.Key;

  /// <inheritdoc/>
  public override bool Equals(object obj) => obj is Metric other && Equals(other);

  /// <inheritdoc/>
  public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

  /// <inheritdoc/>
  public override string ToString() => Key;
}
