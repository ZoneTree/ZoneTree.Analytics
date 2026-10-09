namespace ZoneTree.Analytics;

/// <summary>
/// Data retention options. Null means keep forever.
/// Retention removes whole storage partitions, so data is kept for at most
/// one partition period longer than configured.
/// </summary>
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
  /// How often retention is applied automatically. Default is 1 hour.
  /// Set to null to apply retention only through <see cref="AnalyticsEngine.ApplyRetention"/>.
  /// </summary>
  public TimeSpan? CheckInterval { get; set; } = TimeSpan.FromHours(1);

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
