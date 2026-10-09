namespace ZoneTree.Analytics;

/// <summary>
/// Raised when the options conflict with the storage format of an existing data directory,
/// for example a different reporting time zone or sketch precision.
/// </summary>
public sealed class AnalyticsConfigurationException : Exception
{
  /// <summary>Creates the exception.</summary>
  public AnalyticsConfigurationException(string message) : base(message)
  {
  }
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

/// <summary>
/// Raised when a query or funnel runs longer than <see cref="QueryOptions.Timeout"/>.
/// Narrow the range, use dimensions that have rollups, or raise the timeout.
/// </summary>
public sealed class AnalyticsQueryTimeoutException : TimeoutException
{
  /// <summary>Creates the exception.</summary>
  public AnalyticsQueryTimeoutException(TimeSpan timeout)
      : base($"The query did not complete within {timeout.TotalSeconds:0.###} seconds (QueryOptions.Timeout). " +
          "Narrow the range, use dimensions that have rollups, or raise the timeout.")
  {
    Timeout = timeout;
  }

  /// <summary>The timeout that was exceeded.</summary>
  public TimeSpan Timeout { get; }
}
