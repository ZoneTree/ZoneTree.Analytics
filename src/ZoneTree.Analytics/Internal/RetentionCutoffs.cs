using System.Globalization;

namespace ZoneTree.Analytics.Internal;

/// <summary>
/// The oldest data each tier keeps at a point in time, derived from <see cref="RetentionOptions"/>.
/// <see cref="long.MinValue"/> means the tier is kept forever.
/// </summary>
/// <remarks>
/// One definition serves every consumer: retention drops partitions that end before a cutoff,
/// ingestion skips late events older than a cutoff (writing them would recreate partitions that
/// retention already dropped), and queries only plan reads at or after a cutoff.
/// </remarks>
/// <param name="RawEvents">The raw event cutoff (UTC ticks).</param>
/// <param name="MinuteRollups">The minute rollup cutoff (UTC ticks).</param>
/// <param name="HourRollups">The hourly rollup cutoff (UTC ticks).</param>
/// <param name="DayRollups">The daily rollup cutoff (UTC ticks).</param>
/// <param name="DayRollupsLocalDay">The daily rollup cutoff as a local day of the reporting time zone.</param>
readonly record struct RetentionCutoffs(
    long RawEvents,
    long MinuteRollups,
    long HourRollups,
    long DayRollups,
    long DayRollupsLocalDay)
{
  /// <summary>Every tier is kept forever.</summary>
  public static RetentionCutoffs None { get; } =
      new(long.MinValue, long.MinValue, long.MinValue, long.MinValue, long.MinValue);

  public static RetentionCutoffs Compute(RetentionOptions retention, ReportingCalendar calendar, long nowUtcTicks)
  {
    var day = Cutoff(retention.DayRollups, nowUtcTicks);
    return new(
        Cutoff(retention.RawEvents, nowUtcTicks),
        Cutoff(retention.MinuteRollups, nowUtcTicks),
        Cutoff(retention.HourRollups, nowUtcTicks),
        day,
        day == long.MinValue ? long.MinValue : calendar.LocalDay(day));
  }

  static long Cutoff(TimeSpan? retention, long nowUtcTicks) =>
      retention.HasValue ? nowUtcTicks - Math.Min(retention.Value.Ticks, nowUtcTicks) : long.MinValue;

  /// <summary>
  /// The UTC cutoff of a rollup granularity.
  /// </summary>
  public long Of(Granularity granularity) => granularity switch
  {
    Granularity.Minute => MinuteRollups,
    Granularity.Hour => HourRollups,
    _ => DayRollups
  };

  /// <summary>
  /// True when a rollup row is within the retention of its granularity. Minute and hour rows
  /// are compared by event time, day rows by their local day bucket.
  /// </summary>
  public bool KeepsRollup(Granularity granularity, long utcTicks, long bucket) =>
      granularity == Granularity.Day ? bucket >= DayRollupsLocalDay : utcTicks >= Of(granularity);

  /// <summary>
  /// A query plan warning when a raw event read starting at <paramref name="fromUtcTicks"/>
  /// reaches before raw event retention; otherwise null.
  /// </summary>
  public string RawEventsWarning(long fromUtcTicks)
  {
    if (fromUtcTicks >= RawEvents)
      return null;
    var cutoff = new DateTime(RawEvents, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    return $"Raw events before {cutoff} are outside retention; the result may be incomplete.";
  }
}
