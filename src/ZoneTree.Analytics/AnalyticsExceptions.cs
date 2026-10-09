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
