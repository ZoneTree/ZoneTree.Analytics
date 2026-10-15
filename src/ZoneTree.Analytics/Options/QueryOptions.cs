namespace ZoneTree.Analytics;

/// <summary>
/// Query options.
/// </summary>
public sealed class QueryOptions
{
  /// <summary>
  /// The longest a query or funnel may run before it is cancelled with an
  /// <see cref="AnalyticsQueryTimeoutException"/>. It protects the host application from
  /// expensive ad-hoc queries, for example long raw event scans requested through a reporting
  /// API. Null means no limit. Default is 30 seconds.
  /// Must not exceed approximately 49.7 days, the cancellation timer's maximum delay. The timer
  /// counts whole milliseconds, so a timeout under one millisecond expires before the query starts.
  /// </summary>
  public TimeSpan? Timeout { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// The most threads one raw event scan uses: queries, segments and funnels, the live visitor
  /// count and visitor erasure. Ranges are split into time slices that are scanned concurrently
  /// and merged; 1 scans on the calling thread only. Rollup reads are not affected. Lower it when
  /// many raw queries run at the same time and should share the processors. Default is the
  /// number of processors.
  /// </summary>
  public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

  /// <summary>
  /// The most group-by dimensions of one query. Default is 8.
  /// </summary>
  public int MaxGroupBy { get; set; } = 8;

  /// <summary>
  /// The most aggregate groups held while reading one query period. Default is 100,000.
  /// Each time bucket and dimension combination counts separately for each metric source;
  /// temporary groups of parallel raw scans also count until merged. The limit is enforced
  /// before allocating a group, regardless of Limit, Having or LimitPerBucket. Exceeding it
  /// raises <see cref="AnalyticsQueryException"/>. Comparison periods use separate budgets.
  /// </summary>
  public int MaxGroups { get; set; } = 100_000;

  /// <summary>
  /// The most time buckets of one query: longer time series are rejected rather than truncated.
  /// Default is 100,000, for example 69 days of minutes or 11 years of hours.
  /// </summary>
  public int MaxTimeBuckets { get; set; } = 100_000;

  /// <summary>
  /// The most rows filling a time series may create: the groups with data times the buckets
  /// (see <see cref="AnalyticsQuery.Fill"/>). Larger fills are rejected. Default is 1,000,000.
  /// </summary>
  public int MaxFilledRows { get; set; } = 1_000_000;

  internal QueryOptions Clone() => (QueryOptions)MemberwiseClone();

  internal void Validate()
  {
    if (Timeout <= TimeSpan.Zero)
      throw new ArgumentException("Queries.Timeout must be positive, or null for no limit.");
    if (Timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
      throw new ArgumentException("Queries.Timeout exceeds the cancellation timer's maximum delay (4,294,967,294 milliseconds).");
    if (MaxDegreeOfParallelism < 1)
      throw new ArgumentException("Queries.MaxDegreeOfParallelism must be positive.");
    if (MaxGroupBy < 1)
      throw new ArgumentException("Queries.MaxGroupBy must be positive.");
    if (MaxGroups < 1)
      throw new ArgumentException("Queries.MaxGroups must be positive.");
    if (MaxTimeBuckets < 1)
      throw new ArgumentException("Queries.MaxTimeBuckets must be positive.");
    if (MaxFilledRows < 1)
      throw new ArgumentException("Queries.MaxFilledRows must be positive.");
  }
}
