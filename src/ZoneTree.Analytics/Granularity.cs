namespace ZoneTree.Analytics;

/// <summary>
/// Pre-aggregation (rollup) granularities.
/// </summary>
[Flags]
public enum Granularity
{
  /// <summary>No rollups.</summary>
  None = 0,

  /// <summary>One-minute UTC buckets. Useful for real-time dashboards.</summary>
  Minute = 1,

  /// <summary>One-hour UTC buckets.</summary>
  Hour = 2,

  /// <summary>Calendar days in the configured reporting time zone.</summary>
  Day = 4,
}

/// <summary>
/// Time series bucket sizes of a query.
/// </summary>
public enum TimeInterval
{
  /// <summary>No time series. A single bucket covers the whole query range.</summary>
  None,

  /// <summary>One-minute UTC buckets.</summary>
  Minute,

  /// <summary>One-hour UTC buckets.</summary>
  Hour,

  /// <summary>Calendar days in the reporting time zone.</summary>
  Day,

  /// <summary>Calendar weeks in the reporting time zone.</summary>
  Week,

  /// <summary>Calendar months in the reporting time zone.</summary>
  Month,

  /// <summary>Calendar years in the reporting time zone.</summary>
  Year,
}

/// <summary>
/// Time partitioning of raw event storage.
/// </summary>
public enum PartitionPeriod
{
  /// <summary>One ZoneTree per UTC day. Recommended for high-volume sites.</summary>
  Day,

  /// <summary>One ZoneTree per UTC month.</summary>
  Month,
}
