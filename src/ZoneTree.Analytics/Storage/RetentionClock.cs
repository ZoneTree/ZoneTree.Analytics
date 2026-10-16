using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The current retention cutoffs, read by the deletion predicates of the raw event and rollup
/// stores: a record older than its tier's retention is deleted for every reader as soon as the
/// cutoff passes it, whatever the size of its partition.
/// </summary>
/// <remarks>
/// The predicates run for every record ZoneTree reads or merges, so they read the timestamp
/// straight from the key and compare it with one volatile cutoff.
/// </remarks>
sealed class RetentionClock
{
  readonly RetentionOptions retention;

  readonly ReportingCalendar calendar;

  volatile Snapshot current = new(RetentionCutoffs.None);

  long rawEventsHold = long.MaxValue;

  sealed record Snapshot(RetentionCutoffs Cutoffs);

  public RetentionClock(RetentionOptions retention, ReportingCalendar calendar)
  {
    this.retention = retention;
    this.calendar = calendar;
    Refresh(DateTime.UtcNow.Ticks);
  }

  public RetentionCutoffs Current => current.Cutoffs;

  /// <summary>
  /// Moves the cutoffs to a point in time.
  /// </summary>
  public void Refresh(long nowUtcTicks)
  {
    var cutoffs = At(nowUtcTicks);
    if (cutoffs != current.Cutoffs)
      current = new Snapshot(cutoffs);
  }

  /// <summary>The cutoffs at a point in time, including a raw event hold.</summary>
  public RetentionCutoffs At(long nowUtcTicks)
  {
    var cutoffs = RetentionCutoffs.Compute(retention, calendar, nowUtcTicks);
    var hold = Interlocked.Read(ref rawEventsHold);
    return cutoffs.RawEvents > hold ? cutoffs with { RawEvents = hold } : cutoffs;
  }

  /// <summary>
  /// Keeps the raw events from a point in time on, beyond their retention, until
  /// <see cref="ReleaseRawEvents"/>: a history reconstruction reads them.
  /// </summary>
  public void HoldRawEvents(long fromUtcTicks)
  {
    Interlocked.Exchange(ref rawEventsHold, fromUtcTicks);
    Refresh(DateTime.UtcNow.Ticks);
  }

  /// <summary>Lets raw event retention move on again.</summary>
  public void ReleaseRawEvents()
  {
    Interlocked.Exchange(ref rawEventsHold, long.MaxValue);
    Refresh(DateTime.UtcNow.Ticks);
  }

  public bool IsRawEventExpired(ReadOnlySpan<byte> key) =>
      KeyCodec.ReadRawTicks(key) < current.Cutoffs.RawEvents;

  /// <summary>
  /// True when a rollup row is older than its granularity's retention. Minute and hour buckets
  /// are UTC instants, day buckets local days.
  /// </summary>
  public bool IsRollupExpired(Granularity granularity, ReadOnlySpan<byte> key)
  {
    var cutoffs = current.Cutoffs;
    var cutoff = cutoffs.RollupCutoff(granularity);
    return cutoff != long.MinValue && KeyCodec.ReadRollupBucket(key) < cutoff;
  }
}
