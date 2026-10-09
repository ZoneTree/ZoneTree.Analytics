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

  internal void Validate()
  {
    if (Timeout <= TimeSpan.Zero)
      throw new ArgumentException("Queries.Timeout must be positive, or null for no limit.");
  }
}
