using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Ingestion;

/// <summary>
/// The oldest time each data tier still accepts writes for. Late events older than a tier's
/// retention are not written to that tier: retention would only drop them again, and writing
/// them would recreate partitions that retention already dropped. Owned by one worker.
/// </summary>
sealed class RetentionCutoffs
{
  readonly RetentionOptions retention;

  readonly ReportingCalendar calendar;

  long nextRefresh = long.MinValue;

  long raw;

  long minute;

  long hour;

  long localDay;

  public RetentionCutoffs(RetentionOptions retention, ReportingCalendar calendar)
  {
    this.retention = retention;
    this.calendar = calendar;
  }

  void Refresh()
  {
    var tick = Environment.TickCount64;
    if (tick < nextRefresh)
      return;
    nextRefresh = tick + 1000;
    var now = DateTime.UtcNow.Ticks;
    static long Cutoff(TimeSpan? retention, long now) =>
        retention.HasValue ? now - Math.Min(retention.Value.Ticks, now) : long.MinValue;
    raw = Cutoff(retention.RawEvents, now);
    minute = Cutoff(retention.MinuteRollups, now);
    hour = Cutoff(retention.HourRollups, now);
    localDay = retention.DayRollups.HasValue ? calendar.LocalDay(Cutoff(retention.DayRollups, now)) : long.MinValue;
  }

  /// <summary>
  /// True when a raw event at <paramref name="utcTicks"/> is within raw event retention.
  /// </summary>
  public bool KeepsRawEvent(long utcTicks)
  {
    Refresh();
    return utcTicks >= raw;
  }

  /// <summary>
  /// True when a rollup row is within the retention of its granularity. Minute and hour rows
  /// are compared by event time, day rows by their local day.
  /// </summary>
  public bool KeepsRollup(Granularity granularity, long utcTicks, long bucket)
  {
    Refresh();
    return granularity switch
    {
      Granularity.Minute => utcTicks >= minute,
      Granularity.Hour => utcTicks >= hour,
      _ => bucket >= localDay
    };
  }
}
