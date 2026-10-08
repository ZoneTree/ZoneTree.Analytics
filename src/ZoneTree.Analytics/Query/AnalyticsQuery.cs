namespace ZoneTree.Analytics;

/// <summary>
/// Where a query reads its data from.
/// </summary>
public enum QuerySource
{
  /// <summary>
  /// Rollups when a rollup covers the query's dimensions, raw events otherwise.
  /// Ranges that rollups cannot cover exactly are completed from raw events.
  /// </summary>
  Auto,

  /// <summary>Only rollups.</summary>
  Rollups,

  /// <summary>Only raw events.</summary>
  RawEvents,
}

/// <summary>
/// An analytics query: one event, a time range, optional time series interval,
/// group-by dimensions, filters, ordering and limit.
/// </summary>
/// <example>
/// <code>
/// var topPages = engine.Query(new AnalyticsQuery()
///     .Between(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow)
///     .GroupBy(StandardDimensions.Page)
///     .Where(StandardDimensions.Country, "DE")
///     .Top(10));
/// </code>
/// </example>
public sealed class AnalyticsQuery
{
  /// <summary>
  /// The site. Defaults to <see cref="AnalyticsOptions.DefaultSite"/>.
  /// </summary>
  public string Site { get; set; }

  /// <summary>
  /// The event to analyze. Default is <see cref="StandardEvents.PageView"/>.
  /// Use <see cref="StandardEvents.Session"/> for session metrics.
  /// </summary>
  public string Event { get; set; } = StandardEvents.PageView;

  /// <summary>
  /// Inclusive start of the range. Local and unspecified times are treated as described
  /// in <see cref="DateTimeKind"/>: local times are converted to UTC, unspecified times are UTC.
  /// </summary>
  public DateTime From { get; set; }

  /// <summary>
  /// Exclusive end of the range.
  /// </summary>
  public DateTime To { get; set; }

  /// <summary>
  /// Time series interval. <see cref="TimeInterval.None"/> returns one bucket for the range.
  /// </summary>
  public TimeInterval Interval { get; set; }

  /// <summary>
  /// Dimensions to group by.
  /// </summary>
  public List<string> GroupByDimensions { get; set; } = [];

  /// <summary>
  /// Dimension filters. All filters must match.
  /// </summary>
  public List<DimensionFilter> Filters { get; set; } = [];

  /// <summary>
  /// Result ordering. Defaults to bucket order for time series, otherwise to event count descending.
  /// </summary>
  public QueryOrder OrderBy { get; set; }

  /// <summary>
  /// Maximum number of rows. Null returns all rows.
  /// </summary>
  public int? Limit { get; set; }

  /// <summary>
  /// Adds rows for empty buckets of a time series without group-by dimensions. Default is true.
  /// </summary>
  public bool FillEmptyBuckets { get; set; } = true;

  /// <summary>
  /// Data source selection. Default is <see cref="QuerySource.Auto"/>.
  /// </summary>
  public QuerySource Source { get; set; } = QuerySource.Auto;

  /// <summary>Sets the site.</summary>
  public AnalyticsQuery ForSite(string site)
  {
    Site = site;
    return this;
  }

  /// <summary>Sets the event.</summary>
  public AnalyticsQuery ForEvent(string eventName)
  {
    Event = eventName;
    return this;
  }

  /// <summary>Sets the range [from, to).</summary>
  public AnalyticsQuery Between(DateTime from, DateTime to)
  {
    From = from;
    To = to;
    return this;
  }

  /// <summary>Sets the time series interval.</summary>
  public AnalyticsQuery Every(TimeInterval interval)
  {
    Interval = interval;
    return this;
  }

  /// <summary>Adds group-by dimensions.</summary>
  public AnalyticsQuery GroupBy(params string[] dimensions)
  {
    GroupByDimensions.AddRange(dimensions);
    return this;
  }

  /// <summary>Adds a filter.</summary>
  public AnalyticsQuery Where(DimensionFilter filter)
  {
    Filters.Add(filter);
    return this;
  }

  /// <summary>Adds an equality filter.</summary>
  public AnalyticsQuery Where(string dimension, string value)
  {
    Filters.Add(DimensionFilter.Equal(dimension, value));
    return this;
  }

  /// <summary>Adds an inequality filter.</summary>
  public AnalyticsQuery WhereNot(string dimension, string value)
  {
    Filters.Add(DimensionFilter.NotEqual(dimension, value));
    return this;
  }

  /// <summary>Sets the ordering.</summary>
  public AnalyticsQuery Order(QueryOrder order)
  {
    OrderBy = order;
    return this;
  }

  /// <summary>Limits the number of rows.</summary>
  public AnalyticsQuery Top(int limit)
  {
    Limit = limit;
    return this;
  }

  /// <summary>Sets the data source.</summary>
  public AnalyticsQuery UseSource(QuerySource source)
  {
    Source = source;
    return this;
  }
}

/// <summary>
/// Filter operators.
/// </summary>
public enum FilterOperator
{
  /// <summary>Equal to one of the values.</summary>
  Equals,

  /// <summary>Not equal to any of the values.</summary>
  NotEquals,

  /// <summary>Contains one of the values.</summary>
  Contains,

  /// <summary>Contains none of the values.</summary>
  NotContains,

  /// <summary>Starts with one of the values.</summary>
  StartsWith,

  /// <summary>Ends with one of the values.</summary>
  EndsWith,
}

/// <summary>
/// A filter on a dimension. Only events that carry the dimension can match,
/// including for negative operators. An empty value means "known to be empty",
/// for example direct traffic has an empty referrer.
/// </summary>
public sealed class DimensionFilter
{
  /// <summary>
  /// Creates a filter.
  /// </summary>
  public DimensionFilter(string dimension, FilterOperator op, params string[] values)
  {
    if (string.IsNullOrWhiteSpace(dimension))
      throw new ArgumentException("Filter dimension is required.", nameof(dimension));
    if (values == null || values.Length == 0)
      throw new ArgumentException("At least one filter value is required.", nameof(values));
    Dimension = dimension.Trim();
    Operator = op;
    Values = values.Select(x => x ?? "").ToArray();
  }

  /// <summary>The dimension.</summary>
  public string Dimension { get; }

  /// <summary>The operator.</summary>
  public FilterOperator Operator { get; }

  /// <summary>The values. A value matches when it matches any of them.</summary>
  public IReadOnlyList<string> Values { get; }

  /// <summary>Compares case-insensitively. Default is false.</summary>
  public bool IgnoreCase { get; init; }

  /// <summary>Equal to one of the values.</summary>
  public static DimensionFilter Equal(string dimension, params string[] values) =>
      new(dimension, FilterOperator.Equals, values);

  /// <summary>Not equal to any of the values.</summary>
  public static DimensionFilter NotEqual(string dimension, params string[] values) =>
      new(dimension, FilterOperator.NotEquals, values);

  /// <summary>Contains one of the values.</summary>
  public static DimensionFilter Contains(string dimension, params string[] values) =>
      new(dimension, FilterOperator.Contains, values);

  /// <summary>Contains none of the values.</summary>
  public static DimensionFilter NotContains(string dimension, params string[] values) =>
      new(dimension, FilterOperator.NotContains, values);

  /// <summary>Starts with one of the values.</summary>
  public static DimensionFilter StartsWith(string dimension, params string[] values) =>
      new(dimension, FilterOperator.StartsWith, values);

  /// <summary>Ends with one of the values.</summary>
  public static DimensionFilter EndsWith(string dimension, params string[] values) =>
      new(dimension, FilterOperator.EndsWith, values);

  /// <summary>
  /// Evaluates the filter against a dimension value.
  /// </summary>
  public bool Matches(string value)
  {
    value ??= "";
    var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    switch (Operator)
    {
      case FilterOperator.Equals:
        foreach (var v in Values)
        {
          if (string.Equals(value, v, comparison))
            return true;
        }
        return false;
      case FilterOperator.NotEquals:
        foreach (var v in Values)
        {
          if (string.Equals(value, v, comparison))
            return false;
        }
        return true;
      case FilterOperator.Contains:
        foreach (var v in Values)
        {
          if (value.Contains(v, comparison))
            return true;
        }
        return false;
      case FilterOperator.NotContains:
        foreach (var v in Values)
        {
          if (value.Contains(v, comparison))
            return false;
        }
        return true;
      case FilterOperator.StartsWith:
        foreach (var v in Values)
        {
          if (value.StartsWith(v, comparison))
            return true;
        }
        return false;
      case FilterOperator.EndsWith:
        foreach (var v in Values)
        {
          if (value.EndsWith(v, comparison))
            return true;
        }
        return false;
      default:
        return false;
    }
  }

  /// <inheritdoc/>
  public override string ToString() => $"{Dimension} {Operator} [{string.Join(", ", Values)}]";
}

/// <summary>
/// Sort keys.
/// </summary>
public enum OrderKind
{
  /// <summary>Event count.</summary>
  Events,

  /// <summary>Unique visitors.</summary>
  Visitors,

  /// <summary>Sum of a metric.</summary>
  MetricSum,

  /// <summary>Average of a metric.</summary>
  MetricAverage,

  /// <summary>A group-by dimension value.</summary>
  Dimension,

  /// <summary>Time bucket.</summary>
  Bucket,
}

/// <summary>
/// Result ordering.
/// </summary>
/// <param name="Kind">The sort key.</param>
/// <param name="Name">The metric or dimension name for metric and dimension sort keys.</param>
/// <param name="Descending">Sort descending.</param>
public sealed record QueryOrder(OrderKind Kind, string Name = null, bool Descending = true)
{
  /// <summary>By event count.</summary>
  public static QueryOrder ByEvents(bool descending = true) => new(OrderKind.Events, null, descending);

  /// <summary>By unique visitors.</summary>
  public static QueryOrder ByVisitors(bool descending = true) => new(OrderKind.Visitors, null, descending);

  /// <summary>By the sum of a metric.</summary>
  public static QueryOrder ByMetricSum(string metric, bool descending = true) => new(OrderKind.MetricSum, metric, descending);

  /// <summary>By the average of a metric.</summary>
  public static QueryOrder ByMetricAverage(string metric, bool descending = true) => new(OrderKind.MetricAverage, metric, descending);

  /// <summary>By a dimension value.</summary>
  public static QueryOrder ByDimension(string dimension, bool descending = false) => new(OrderKind.Dimension, dimension, descending);

  /// <summary>By time bucket.</summary>
  public static QueryOrder ByBucket(bool descending = false) => new(OrderKind.Bucket, null, descending);
}
