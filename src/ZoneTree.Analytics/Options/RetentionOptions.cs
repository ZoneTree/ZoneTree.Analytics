namespace ZoneTree.Analytics;

/// <summary>
/// Data retention options. Null means keep forever.
/// </summary>
/// <remarks>
/// Records older than retention are deleted for every reader at once: queries never see them,
/// whatever the partitioning. Retention moves in whole UTC days (whole hours for retention under
/// a day), so data is kept for its retention and at most one more day. Expired records leave the
/// disk when retention runs (<see cref="CheckInterval"/>): partitions that expired as a whole are
/// dropped, and the others are merged without their expired records, which rewrites at most the
/// oldest part of each key range once a day. In trees larger than
/// <see cref="StoreOptions.DiskSegmentMaxItemCount"/>, records in bottom segments leave the disk
/// when the bottom segments are next merged.
/// </remarks>
public sealed class RetentionOptions
{
  /// <summary>
  /// Retention of raw events. Default is 90 days.
  /// </summary>
  public TimeSpan? RawEvents { get; set; } = TimeSpan.FromDays(90);

  /// <summary>
  /// Retention of minute rollups. Default is 2 days.
  /// </summary>
  public TimeSpan? MinuteRollups { get; set; } = TimeSpan.FromDays(2);

  /// <summary>
  /// Retention of hourly rollups. Default is 400 days.
  /// </summary>
  public TimeSpan? HourRollups { get; set; } = TimeSpan.FromDays(400);

  /// <summary>
  /// Retention of daily rollups. Default is forever.
  /// </summary>
  public TimeSpan? DayRollups { get; set; }

  /// <summary>
  /// How often expired data is removed from disk. Default is 1 hour; a tier rewrites data only
  /// when its cutoff has moved, once a day (once an hour for retention under a day). Set to null
  /// to remove expired data only through <see cref="AnalyticsEngine.ApplyRetention"/>; queries
  /// hide it either way.
  /// </summary>
  public TimeSpan? CheckInterval { get; set; } = TimeSpan.FromHours(1);

  internal RetentionOptions Clone() => (RetentionOptions)MemberwiseClone();

  internal void Validate()
  {
    static void RequirePositive(TimeSpan? value, string name)
    {
      if (value <= TimeSpan.Zero)
        throw new ArgumentException($"Retention.{name} must be positive, or null to keep data forever.");
    }
    RequirePositive(RawEvents, nameof(RawEvents));
    RequirePositive(MinuteRollups, nameof(MinuteRollups));
    RequirePositive(HourRollups, nameof(HourRollups));
    RequirePositive(DayRollups, nameof(DayRollups));
    if (CheckInterval <= TimeSpan.Zero)
      throw new ArgumentException("Retention.CheckInterval must be positive, or null to apply retention manually.");
  }
}
