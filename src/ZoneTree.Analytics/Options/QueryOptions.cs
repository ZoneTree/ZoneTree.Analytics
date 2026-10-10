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

  internal QueryOptions Clone() => (QueryOptions)MemberwiseClone();

  internal void Validate()
  {
    if (Timeout <= TimeSpan.Zero)
      throw new ArgumentException("Queries.Timeout must be positive, or null for no limit.");
    if (MaxDegreeOfParallelism < 1)
      throw new ArgumentException("Queries.MaxDegreeOfParallelism must be positive.");
  }
}
