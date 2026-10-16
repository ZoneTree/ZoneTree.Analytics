using System.Globalization;

namespace ZoneTree.Analytics;

/// <summary>
/// One result row: a time bucket and group, with one value per query metric.
/// </summary>
public sealed class AnalyticsRow
{
  readonly AnalyticsResultShape shape;

  readonly double[] values;

  internal AnalyticsRow(AnalyticsResultShape shape, DateTimeOffset? bucket, string[] dimensions, double[] values)
  {
    this.shape = shape;
    Bucket = bucket;
    Dimensions = dimensions;
    this.values = values;
  }

  /// <summary>
  /// Start of the time bucket, or null without an interval. Minute and hour buckets are UTC;
  /// day and coarser buckets carry the offset of the query's time zone.
  /// </summary>
  public DateTimeOffset? Bucket { get; }

  /// <summary>
  /// Group-by values, in the order of the query's group-by dimensions. Null means the events
  /// of this row do not carry the dimension.
  /// </summary>
  public IReadOnlyList<string> Dimensions { get; }

  /// <summary>
  /// Metric values, in the order of the query's metrics.
  /// </summary>
  public IReadOnlyList<double> Values => values;

  /// <summary>
  /// The same row of the comparison period, when the query has a <see cref="AnalyticsQuery.Compare"/> period.
  /// Null when the comparison period has no such row.
  /// </summary>
  public AnalyticsRow Previous { get; internal set; }

  /// <summary>
  /// The value of a query metric.
  /// </summary>
  /// <exception cref="ArgumentException">The metric is not part of the query.</exception>
  public double this[Metric metric] => values[shape.IndexOf(metric)];

  /// <summary>
  /// The value of a group-by dimension, or null when missing.
  /// </summary>
  /// <exception cref="ArgumentException">The dimension is not a group-by dimension.</exception>
  public string this[string dimension] => Dimensions[shape.IndexOfDimension(dimension)];

  /// <summary>
  /// The relative change of a metric from the comparison period: (current - previous) / previous.
  /// Null without a comparison row or when the previous value is zero.
  /// </summary>
  public double? Change(Metric metric)
  {
    if (Previous == null)
      return null;
    var previous = Previous[metric];
    if (previous == 0)
      return null;
    return (this[metric] - previous) / previous;
  }

  /// <inheritdoc/>
  public override string ToString()
  {
    var parts = new List<string>();
    if (Bucket.HasValue)
      parts.Add(Bucket.Value.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
    if (Dimensions.Count > 0)
      parts.Add("[" + string.Join(", ", Dimensions.Select(x => x ?? "(none)")) + "]");
    for (var i = 0; i < values.Length; ++i)
      parts.Add(shape.Metrics[i].Key + "=" + values[i].ToString("R", CultureInfo.InvariantCulture));
    return string.Join(" ", parts);
  }
}

/// <summary>
/// Column layout shared by the rows of one result.
/// </summary>
sealed class AnalyticsResultShape
{
  readonly Dictionary<string, int> metricIndexes;

  readonly Dictionary<string, int> dimensionIndexes;

  public AnalyticsResultShape(IReadOnlyList<Metric> metrics, IReadOnlyList<string> groupBy)
  {
    Metrics = metrics;
    GroupBy = groupBy;
    metricIndexes = new(StringComparer.Ordinal);
    for (var i = 0; i < metrics.Count; ++i)
      metricIndexes.TryAdd(metrics[i].Key, i);
    dimensionIndexes = new(StringComparer.Ordinal);
    for (var i = 0; i < groupBy.Count; ++i)
      dimensionIndexes.TryAdd(groupBy[i], i);
  }

  public IReadOnlyList<Metric> Metrics { get; }

  public IReadOnlyList<string> GroupBy { get; }

  public int IndexOf(Metric metric)
  {
    ArgumentNullException.ThrowIfNull(metric);
    return metricIndexes.TryGetValue(metric.Key, out var index)
        ? index
        : throw new ArgumentException($"The metric '{metric.Key}' is not part of the query.", nameof(metric));
  }

  public int IndexOfDimension(string dimension)
  {
    return dimension != null && dimensionIndexes.TryGetValue(dimension, out var index)
        ? index
        : throw new ArgumentException($"'{dimension}' is not a group-by dimension of the query.", nameof(dimension));
  }
}

/// <summary>
/// The result of a query.
/// </summary>
public sealed class AnalyticsResult
{
  internal AnalyticsResult(
      AnalyticsResultShape shape,
      TimeInterval interval,
      DateTimeOffset from,
      DateTimeOffset to,
      TimeZoneInfo timeZone,
      IReadOnlyList<AnalyticsRow> rows,
      int totalRows,
      AnalyticsRow totals,
      QueryPlan plan)
  {
    Metrics = shape.Metrics;
    GroupBy = shape.GroupBy;
    Interval = interval;
    From = from;
    To = to;
    TimeZone = timeZone;
    Rows = rows;
    TotalRows = totalRows;
    Totals = totals;
    Plan = plan;
  }

  /// <summary>The metrics, in column order.</summary>
  public IReadOnlyList<Metric> Metrics { get; }

  /// <summary>The group-by dimensions, in column order.</summary>
  public IReadOnlyList<string> GroupBy { get; }

  /// <summary>The time series interval.</summary>
  public TimeInterval Interval { get; }

  /// <summary>The resolved inclusive start of the range, in the query's time zone.</summary>
  public DateTimeOffset From { get; }

  /// <summary>The resolved exclusive end of the range, in the query's time zone.</summary>
  public DateTimeOffset To { get; }

  /// <summary>The time zone of the query.</summary>
  public TimeZoneInfo TimeZone { get; }

  /// <summary>The rows after ordering, offset and limit.</summary>
  public IReadOnlyList<AnalyticsRow> Rows { get; }

  /// <summary>The number of rows before offset and limit, for paging.</summary>
  public int TotalRows { get; }

  /// <summary>
  /// Totals over the whole range and every group, before offset and limit. Unique visitors are
  /// de-duplicated across rows and buckets. With a comparison, <see cref="AnalyticsRow.Previous"/>
  /// holds the totals of the comparison period.
  /// </summary>
  public AnalyticsRow Totals { get; }

  /// <summary>How the query was answered.</summary>
  public QueryPlan Plan { get; }

  /// <summary>The result of the comparison period, when the query has a <see cref="AnalyticsQuery.Compare"/> period.</summary>
  public AnalyticsResult Comparison { get; internal set; }
}

/// <summary>
/// The data a part of a query was answered from.
/// </summary>
public enum PlanSource
{
  /// <summary>Daily rollups.</summary>
  DayRollups,

  /// <summary>Hourly rollups.</summary>
  HourRollups,

  /// <summary>Minute rollups.</summary>
  MinuteRollups,

  /// <summary>Raw events.</summary>
  RawEvents,

  /// <summary>A full-payload ordered secondary dimension index over raw events.</summary>
  DimensionIndex,
}

/// <summary>
/// One part of a query plan: an event, a source and a time range.
/// </summary>
/// <param name="Event">The event read.</param>
/// <param name="Source">The data source.</param>
/// <param name="From">The inclusive start (UTC).</param>
/// <param name="To">The exclusive end (UTC).</param>
public sealed record QueryPlanStep(string Event, PlanSource Source, DateTimeOffset From, DateTimeOffset To)
{
  /// <summary>The dimension used for a covering index read; null for other sources.</summary>
  public string IndexDimension { get; init; }
}

/// <summary>A missing definition that would make this event eligible for rollup execution.</summary>
/// <remarks>
/// Add <see cref="Definition"/> to <see cref="RollupOptions.Definitions"/> before tracking new data.
/// Existing raw events are not backfilled. Retention, active granularities and range boundaries
/// can still require raw reads. Suggestions do not affect plan completeness.
/// </remarks>
/// <param name="Event">The event whose dimensions have no matching rollup.</param>
/// <param name="Definition">The exact stored dimensions required, restricted to this event.</param>
public sealed record RollupSuggestion(string Event, RollupDefinition Definition);

/// <summary>
/// How a query was answered.
/// </summary>
public sealed class QueryPlan
{
  internal QueryPlan(IReadOnlyList<QueryPlanStep> steps, IReadOnlyList<string> warnings,
      IReadOnlyList<RollupSuggestion> rollupSuggestions = null, IReadOnlyList<string> notices = null)
  {
    Steps = steps;
    Warnings = warnings;
    RollupSuggestions = rollupSuggestions ?? Array.Empty<RollupSuggestion>();
    Notices = notices ?? Array.Empty<string>();
  }

  /// <summary>The plan steps.</summary>
  public IReadOnlyList<QueryPlanStep> Steps { get; }

  /// <summary>Missing rollup definitions for automatic raw fallbacks; empty for other causes.</summary>
  public IReadOnlyList<RollupSuggestion> RollupSuggestions { get; }

  /// <summary>
  /// Conditions that may make the result incomplete or approximate, for example a range outside
  /// raw event retention.
  /// </summary>
  public IReadOnlyList<string> Warnings { get; }

  /// <summary>
  /// Background work that made the query read slower sources without changing its result, for
  /// example an index that is still being built after the engine opened. Notices do not affect
  /// <see cref="IsComplete"/>; <see cref="AnalyticsEngine.WaitForIndexesAsync"/> waits for that work.
  /// </summary>
  public IReadOnlyList<string> Notices { get; }

  /// <summary>True when the result covers the whole range exactly.</summary>
  public bool IsComplete => Warnings.Count == 0;

  /// <summary>True when rollups contributed.</summary>
  public bool UsedRollups => Steps.Any(x => x.Source is PlanSource.DayRollups or PlanSource.HourRollups or PlanSource.MinuteRollups);

  /// <summary>True when raw events contributed.</summary>
  public bool UsedRawEvents => Steps.Any(x => x.Source is PlanSource.RawEvents or PlanSource.DimensionIndex);

  /// <summary>True when full-payload dimension indexes contributed.</summary>
  public bool UsedDimensionIndexes => Steps.Any(x => x.Source == PlanSource.DimensionIndex);

  /// <summary>True when covering equality indexes of rollups contributed.</summary>
  public bool UsedRollupIndexes => Steps.Any(x => x.IndexDimension != null &&
      x.Source is PlanSource.DayRollups or PlanSource.HourRollups or PlanSource.MinuteRollups);
}
