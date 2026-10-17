using System.Globalization;

namespace ZoneTree.Analytics.Internal;

/// <summary>
/// The oldest data each tier keeps at a point in time, derived from <see cref="RetentionOptions"/>.
/// <see cref="long.MinValue"/> means the tier is kept forever.
/// </summary>
/// <remarks>
/// <para>
/// One definition serves every consumer: the stores treat older records as deleted, retention
/// drops partitions that end before a cutoff and removes older records from the others,
/// ingestion skips late events older than a cutoff, and queries only plan reads at or after a
/// cutoff.
/// </para>
/// <para>
/// Cutoffs move in whole UTC days, or whole hours for retention shorter than a day: data is
/// kept for its retention and at most one more day (or hour). Removing expired records from disk
/// rewrites the oldest part of each key range once per step, rather than on every merge.
/// </para>
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

  static long Cutoff(TimeSpan? retention, long nowUtcTicks)
  {
    if (!retention.HasValue)
      return long.MinValue;
    var cutoff = nowUtcTicks - Math.Min(retention.Value.Ticks, nowUtcTicks);
    var step = retention.Value.Ticks >= TimeSpan.TicksPerDay ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour;
    return ReportingCalendar.FloorTicks(cutoff, step);
  }

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
  /// The cutoff in the time coordinate rollup keys of a granularity hold: UTC for minute and
  /// hour buckets, the local day for day buckets.
  /// </summary>
  public long RollupCutoff(Granularity granularity) =>
      granularity == Granularity.Day ? DayRollupsLocalDay : Of(granularity);

  /// <summary>
  /// These cutoffs with the cutoff of a rollup granularity taken from <paramref name="source"/>.
  /// </summary>
  public RetentionCutoffs WithRollup(Granularity granularity, RetentionCutoffs source) => granularity switch
  {
    Granularity.Minute => this with { MinuteRollups = source.MinuteRollups },
    Granularity.Hour => this with { HourRollups = source.HourRollups },
    _ => this with { DayRollups = source.DayRollups, DayRollupsLocalDay = source.DayRollupsLocalDay }
  };

  /// <summary>
  /// True when a rollup row is within the retention of its granularity. Minute and hour rows
  /// are compared by event time, day rows by their local day bucket.
  /// </summary>
  public bool KeepsRollup(Granularity granularity, long utcTicks, long bucket) =>
      (granularity == Granularity.Day ? bucket : utcTicks) >= RollupCutoff(granularity);

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
