namespace ZoneTree.Analytics;

/// <summary>
/// The base class of the exceptions raised by ZoneTree.Analytics.
/// </summary>
public class AnalyticsException : Exception
{
  /// <summary>Creates the exception.</summary>
  public AnalyticsException()
  {
  }

  /// <summary>Creates the exception with a message.</summary>
  public AnalyticsException(string message) : base(message)
  {
  }

  /// <summary>Creates the exception with a message and the exception that caused it.</summary>
  public AnalyticsException(string message, Exception innerException) : base(message, innerException)
  {
  }
}

/// <summary>
/// Raised when the options conflict with the storage format of an existing data directory,
/// for example a different reporting time zone or sketch precision, or when the engine
/// metadata is damaged.
/// </summary>
public sealed class AnalyticsConfigurationException : AnalyticsException
{
  /// <summary>Creates the exception.</summary>
  public AnalyticsConfigurationException()
  {
  }

  /// <summary>Creates the exception with a message.</summary>
  public AnalyticsConfigurationException(string message) : base(message)
  {
  }

  /// <summary>Creates the exception with a message and the exception that caused it.</summary>
  public AnalyticsConfigurationException(string message, Exception innerException) : base(message, innerException)
  {
  }
}

/// <summary>
/// Raised when a query is invalid or cannot be answered with the configured storage.
/// </summary>
public class AnalyticsQueryException : AnalyticsException
{
  /// <summary>Creates the exception.</summary>
  public AnalyticsQueryException()
  {
  }

  /// <summary>Creates the exception with a message.</summary>
  public AnalyticsQueryException(string message) : base(message)
  {
  }

  /// <summary>Creates the exception with a message and the exception that caused it.</summary>
  public AnalyticsQueryException(string message, Exception innerException) : base(message, innerException)
  {
  }
}

/// <summary>
/// Raised when a query runs longer than <see cref="QueryOptions.Timeout"/>.
/// Narrow the range, use dimensions that have rollups, or raise the timeout.
/// </summary>
public sealed class AnalyticsQueryTimeoutException : AnalyticsQueryException
{
  /// <summary>Creates the exception.</summary>
  public AnalyticsQueryTimeoutException()
  {
  }

  /// <summary>Creates the exception with a message.</summary>
  public AnalyticsQueryTimeoutException(string message) : base(message)
  {
  }

  /// <summary>Creates the exception with a message and the exception that caused it.</summary>
  public AnalyticsQueryTimeoutException(string message, Exception innerException) : base(message, innerException)
  {
  }

  /// <summary>Creates the exception for a query that exceeded a timeout.</summary>
  public AnalyticsQueryTimeoutException(TimeSpan timeout)
      : base($"The query did not complete within {timeout.TotalSeconds:0.###} seconds (QueryOptions.Timeout). " +
          "Narrow the range, use dimensions that have rollups, or raise the timeout.")
  {
    Timeout = timeout;
  }

  /// <summary>The timeout that was exceeded, when known.</summary>
  public TimeSpan? Timeout { get; }
}
