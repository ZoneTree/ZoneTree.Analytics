namespace ZoneTree.Analytics;

/// <summary>
/// Where a query reads its data from.
/// </summary>
public enum QuerySource
{
  /// <summary>
  /// Rollups where a rollup covers the query's dimensions, raw events otherwise.
  /// Ranges that rollups cannot cover exactly are completed from raw events.
  /// </summary>
  Auto,

  /// <summary>Only rollups.</summary>
  Rollups,

  /// <summary>Only raw events.</summary>
  RawEvents,
}

/// <summary>
/// An analytics query.
/// </summary>
/// <remarks>
/// <para>
/// A query aggregates a base <see cref="Event"/> over a <see cref="Range"/>, optionally as a
/// time series, grouped by dimensions and filtered. <see cref="Metrics"/> select what to
/// compute. Metrics may read other events than the base event: session metrics read sessions,
/// <see cref="Metric.ConversionRate"/> reads its goal event, and so on. The engine runs one
/// sub-query per event and joins them on the time bucket and group values.
/// </para>
/// <para>
/// A dimension that an event does not carry groups under <c>null</c>, so grouped totals always
/// equal ungrouped totals.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var result = await analytics.QueryAsync(new AnalyticsQuery
/// {
///     Range = DateRange.LastDays(30),
///     GroupBy = { StandardDimensions.Source },
///     Metrics = { Metric.Visitors, Metric.BounceRate, Metric.ConversionRate("signup") },
///     OrderBy = { Order.By(Metric.Visitors) },
///     Limit = 10,
/// });
/// </code>
/// </example>
public sealed class AnalyticsQuery
{
  /// <summary>
  /// The site. Defaults to <see cref="AnalyticsOptions.DefaultSite"/>.
  /// </summary>
  public string Site { get; set; }

  /// <summary>
  /// The base event. Default is <see cref="StandardEvents.PageView"/>.
  /// <see cref="Metric.Events"/>, <see cref="Metric.Visitors"/> and value metrics such as
  /// <see cref="Metric.Sum(string, string)"/> read this event.
  /// </summary>
  public string Event { get; set; } = StandardEvents.PageView;

  /// <summary>
  /// The time range. Default is the last 30 days including today.
  /// </summary>
  public DateRange Range { get; set; } = DateRange.LastDays(30);

  /// <summary>
  /// The time zone of calendar ranges and day, week, month and year buckets.
  /// Defaults to the engine's reporting time zone. Other time zones are answered from hourly
  /// rollups (and minute rollups or raw events for offsets that are not whole hours).
  /// </summary>
  public TimeZoneInfo TimeZone { get; set; }

  /// <summary>
  /// Time series interval. <see cref="TimeInterval.None"/> returns one bucket for the range.
  /// </summary>
  public TimeInterval Interval { get; set; }

  /// <summary>
  /// Dimensions to group by.
  /// </summary>
  public List<string> GroupBy { get; } = [];

  /// <summary>
  /// Filters on the rows of every sub-query. All filters must match.
  /// </summary>
  public List<Filter> Filters { get; } = [];

  /// <summary>
  /// Behavioral segments: only visitors or sessions that did (or did not do) something are
  /// counted. Segments are evaluated over raw events.
  /// </summary>
  public List<Segment> Segments { get; } = [];

  /// <summary>
  /// The metrics to compute. Default is <see cref="Metric.Events"/> and <see cref="Metric.Visitors"/>.
  /// </summary>
  public List<Metric> Metrics { get; } = [];

  /// <summary>
  /// Sort keys, applied in order. Default is bucket order for time series, otherwise the first
  /// metric descending.
  /// </summary>
  public List<Order> OrderBy { get; } = [];

  /// <summary>
  /// Maximum number of rows. Null returns all rows.
  /// </summary>
  public int? Limit { get; set; }

  /// <summary>
  /// Number of rows to skip, for paging.
  /// </summary>
  public int Offset { get; set; }

  /// <summary>
  /// Adds rows for empty buckets of a time series without group-by dimensions. Default is true.
  /// </summary>
  public bool FillEmptyBuckets { get; set; } = true;

  /// <summary>
  /// When true (the default), a row is returned for every bucket and group that has data for
  /// any metric, like a full outer join. When false, only buckets and groups that have base
  /// events are returned and the other metrics are joined onto them, like a left join: for
  /// example a page-view report that should not list sources with conversions but no page views.
  /// </summary>
  public bool IncludeRowsWithoutBaseEvent { get; set; } = true;

  /// <summary>
  /// Compares every row with a previous period. The result's rows carry
  /// <see cref="AnalyticsRow.Previous"/>.
  /// </summary>
  public Comparison Compare { get; set; }

  /// <summary>
  /// Data source selection. Default is <see cref="QuerySource.Auto"/>.
  /// </summary>
  public QuerySource Source { get; set; }

  internal AnalyticsQuery CloneForRange(DateRange range)
  {
    var clone = new AnalyticsQuery
    {
      Site = Site,
      Event = Event,
      Range = range,
      TimeZone = TimeZone,
      Interval = Interval,
      Limit = null,
      Offset = 0,
      FillEmptyBuckets = FillEmptyBuckets,
      IncludeRowsWithoutBaseEvent = IncludeRowsWithoutBaseEvent,
      Compare = null,
      Source = Source
    };
    clone.GroupBy.AddRange(GroupBy);
    clone.Filters.AddRange(Filters);
    clone.Segments.AddRange(Segments);
    clone.Metrics.AddRange(Metrics);
    return clone;
  }
}
