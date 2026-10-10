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
/// The period of one storage partition. Each partition is a separate ZoneTree; retention
/// drops whole partitions. See <see cref="PartitioningOptions"/>.
/// </summary>
public enum PartitionPeriod
{
  /// <summary>One ZoneTree per day.</summary>
  Day,

  /// <summary>One ZoneTree per month.</summary>
  Month,

  /// <summary>One ZoneTree per year.</summary>
  Year,
}
