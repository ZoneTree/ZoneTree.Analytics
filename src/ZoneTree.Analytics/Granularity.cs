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
