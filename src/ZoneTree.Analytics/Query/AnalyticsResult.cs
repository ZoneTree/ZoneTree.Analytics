namespace ZoneTree.Analytics;

/// <summary>
/// The aggregate of one metric.
/// </summary>
/// <remarks>
/// <see cref="Count"/> is the number of measured values. Engine-generated session
/// metrics are maintained with additive deltas, so only their <see cref="Sum"/>
/// is meaningful.
/// </remarks>
/// <param name="Sum">The sum of values.</param>
/// <param name="Count">The number of measured values.</param>
/// <param name="Min">The smallest value. Zero when <paramref name="Count"/> is zero.</param>
/// <param name="Max">The largest value. Zero when <paramref name="Count"/> is zero.</param>
public readonly record struct MetricValue(double Sum, long Count, double Min, double Max)
{
  /// <summary>
  /// The average of the measured values, or zero when there are none.
  /// </summary>
  public double Average => Count == 0 ? 0 : Sum / Count;
}

/// <summary>
/// One result row.
/// </summary>
public sealed class AnalyticsRow
{
  static readonly IReadOnlyDictionary<string, MetricValue> NoMetrics =
      new Dictionary<string, MetricValue>();

  internal AnalyticsRow(
      DateTimeOffset? bucket,
      string[] dimensions,
      long events,
      long visitors,
      IReadOnlyDictionary<string, MetricValue> metrics)
  {
    Bucket = bucket;
    Dimensions = dimensions;
    Events = events;
    Visitors = visitors;
    Metrics = metrics ?? NoMetrics;
  }

  /// <summary>
  /// Start of the time bucket, or null when the query has no interval.
  /// Minute and hour buckets are UTC; day and coarser buckets carry the
  /// reporting time zone offset.
  /// </summary>
  public DateTimeOffset? Bucket { get; }

  /// <summary>
  /// Group-by dimension values, in the order of the query's group-by dimensions.
  /// An empty string is a known empty value, for example direct traffic.
  /// </summary>
  public IReadOnlyList<string> Dimensions { get; }

  /// <summary>
  /// Number of events. For <see cref="StandardEvents.Session"/> rows it is the number of sessions.
  /// </summary>
  public long Events { get; }

  /// <summary>
  /// Unique visitors. Exact for small sets, estimated with about 0.8% standard
  /// error for large sets at the default sketch precision.
  /// </summary>
  public long Visitors { get; }

  /// <summary>
  /// Metric aggregates by name.
  /// </summary>
  public IReadOnlyDictionary<string, MetricValue> Metrics { get; }

  /// <summary>
  /// Returns the sum of a metric, or zero.
  /// </summary>
  public double Sum(string metric) => Metrics.TryGetValue(metric, out var value) ? value.Sum : 0;

  /// <summary>
  /// Returns the aggregate of a metric, or an empty aggregate.
  /// </summary>
  public MetricValue Metric(string metric) => Metrics.TryGetValue(metric, out var value) ? value : default;

  /// <inheritdoc/>
  public override string ToString()
  {
    var bucket = Bucket.HasValue ? Bucket.Value.ToString("u") + " " : "";
    var dimensions = Dimensions.Count > 0 ? "[" + string.Join(", ", Dimensions) + "] " : "";
    return $"{bucket}{dimensions}events={Events} visitors={Visitors}";
  }
}

/// <summary>
/// The result of a query.
/// </summary>
public sealed class AnalyticsResult
{
  internal AnalyticsResult(
      IReadOnlyList<string> groupBy,
      TimeInterval interval,
      IReadOnlyList<AnalyticsRow> rows,
      AnalyticsRow totals,
      bool usedRollups,
      bool usedRawEvents,
      bool mayBeIncomplete,
      DateTime from,
      DateTime to)
  {
    GroupBy = groupBy;
    Interval = interval;
    Rows = rows;
    Totals = totals;
    UsedRollups = usedRollups;
    UsedRawEvents = usedRawEvents;
    MayBeIncomplete = mayBeIncomplete;
    From = from;
    To = to;
  }

  /// <summary>The group-by dimensions.</summary>
  public IReadOnlyList<string> GroupBy { get; }

  /// <summary>The time series interval.</summary>
  public TimeInterval Interval { get; }

  /// <summary>The rows after ordering and limit.</summary>
  public IReadOnlyList<AnalyticsRow> Rows { get; }

  /// <summary>
  /// Totals over every matching row before the limit is applied. Unique visitors
  /// are de-duplicated across rows and buckets.
  /// </summary>
  public AnalyticsRow Totals { get; }

  /// <summary>True when rollups contributed to the result.</summary>
  public bool UsedRollups { get; }

  /// <summary>True when raw events contributed to the result.</summary>
  public bool UsedRawEvents { get; }

  /// <summary>
  /// True when part of the range is outside the configured retention, or could only be
  /// answered by widening it to coarser bucket boundaries.
  /// </summary>
  public bool MayBeIncomplete { get; }

  /// <summary>The effective inclusive start (UTC).</summary>
  public DateTime From { get; }

  /// <summary>The effective exclusive end (UTC).</summary>
  public DateTime To { get; }
}

/// <summary>
/// Raised when a query is invalid or cannot be answered with the configured storage.
/// </summary>
public sealed class AnalyticsQueryException : Exception
{
  /// <summary>Creates the exception.</summary>
  public AnalyticsQueryException(string message) : base(message)
  {
  }
}
