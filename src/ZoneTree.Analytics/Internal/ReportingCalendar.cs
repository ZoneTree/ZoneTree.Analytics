namespace ZoneTree.Analytics.Internal;

/// <summary>
/// Bucket arithmetic. Minute and hour buckets are UTC instants. Day, week, month and
/// year buckets are calendar dates of the reporting time zone, represented by the
/// ticks of the local midnight.
/// </summary>
sealed class ReportingCalendar
{
  readonly TimeZoneInfo timeZone;

  readonly bool isUtc;

  readonly DayOfWeek firstDayOfWeek;

  public ReportingCalendar(TimeZoneInfo timeZone, DayOfWeek firstDayOfWeek)
  {
    this.timeZone = timeZone;
    isUtc = timeZone.Id == TimeZoneInfo.Utc.Id || timeZone.BaseUtcOffset == TimeSpan.Zero &&
        !timeZone.SupportsDaylightSavingTime;
    this.firstDayOfWeek = firstDayOfWeek;
  }

  public TimeZoneInfo TimeZone => timeZone;

  public static long FloorTicks(long ticks, long unit) => ticks - ticks % unit;

  /// <summary>
  /// Returns the local date (as ticks of local midnight) that contains the UTC instant.
  /// </summary>
  public long LocalDay(long utcTicks)
  {
    if (isUtc)
      return FloorTicks(utcTicks, TimeSpan.TicksPerDay);
    var local = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(utcTicks, DateTimeKind.Utc), timeZone);
    return local.Date.Ticks;
  }

  /// <summary>
  /// Returns the UTC instant of the start of the local date.
  /// </summary>
  public long UtcStartOfLocalDay(long localDayTicks)
  {
    if (isUtc)
      return localDayTicks;
    var local = new DateTime(localDayTicks, DateTimeKind.Unspecified);
    // Some time zones skip midnight on DST transitions: the day starts at the first valid time.
    var guard = 0;
    while (timeZone.IsInvalidTime(local) && guard++ < 8)
      local = local.AddMinutes(30);
    if (timeZone.IsAmbiguousTime(local))
    {
      // The first occurrence of an ambiguous time has the larger offset.
      var offsets = timeZone.GetAmbiguousTimeOffsets(local);
      var max = offsets.Max();
      return local.Ticks - max.Ticks;
    }
    return local.Ticks - timeZone.GetUtcOffset(local).Ticks;
  }

  /// <summary>
  /// Returns the bucket key of a UTC instant for the interval.
  /// </summary>
  public long Bucket(long utcTicks, TimeInterval interval)
  {
    return interval switch
    {
      TimeInterval.Minute => FloorTicks(utcTicks, TimeSpan.TicksPerMinute),
      TimeInterval.Hour => FloorTicks(utcTicks, TimeSpan.TicksPerHour),
      TimeInterval.None => 0,
      _ => LocalBucket(LocalDay(utcTicks), interval)
    };
  }

  /// <summary>
  /// Returns the bucket key of a local date for a day-or-coarser interval.
  /// </summary>
  public long LocalBucket(long localDayTicks, TimeInterval interval)
  {
    var date = new DateTime(localDayTicks);
    return interval switch
    {
      TimeInterval.Week => date.AddDays(-(((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7)).Ticks,
      TimeInterval.Month => new DateTime(date.Year, date.Month, 1).Ticks,
      TimeInterval.Year => new DateTime(date.Year, 1, 1).Ticks,
      _ => localDayTicks
    };
  }

  /// <summary>
  /// Converts a bucket key to a labeled instant.
  /// </summary>
  public DateTimeOffset BucketLabel(long bucket, TimeInterval interval)
  {
    if (interval is TimeInterval.Minute or TimeInterval.Hour)
      return new DateTimeOffset(bucket, TimeSpan.Zero);
    var utc = UtcStartOfLocalDay(bucket);
    var offset = TimeSpan.FromTicks(bucket - utc);
    if (offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14))
      offset = TimeSpan.Zero;
    // DateTimeOffset requires whole-minute offsets.
    offset = TimeSpan.FromMinutes(Math.Round(offset.TotalMinutes));
    return new DateTimeOffset(new DateTime(bucket), offset);
  }

  /// <summary>
  /// Returns the next bucket key after the given bucket key.
  /// </summary>
  public long NextBucket(long bucket, TimeInterval interval)
  {
    var date = new DateTime(bucket);
    return interval switch
    {
      TimeInterval.Minute => bucket + TimeSpan.TicksPerMinute,
      TimeInterval.Hour => bucket + TimeSpan.TicksPerHour,
      TimeInterval.Day => bucket + TimeSpan.TicksPerDay,
      TimeInterval.Week => bucket + TimeSpan.TicksPerDay * 7,
      TimeInterval.Month => date.AddMonths(1).Ticks,
      TimeInterval.Year => date.AddYears(1).Ticks,
      _ => long.MaxValue
    };
  }

  /// <summary>
  /// The first local-day boundary (as UTC ticks) at or after the UTC instant.
  /// </summary>
  public long CeilToDayBoundary(long utcTicks)
  {
    var day = LocalDay(utcTicks);
    var start = UtcStartOfLocalDay(day);
    if (start == utcTicks)
      return start;
    return UtcStartOfLocalDay(day + TimeSpan.TicksPerDay);
  }

  /// <summary>
  /// The last local-day boundary (as UTC ticks) at or before the UTC instant.
  /// </summary>
  public long FloorToDayBoundary(long utcTicks)
  {
    var start = UtcStartOfLocalDay(LocalDay(utcTicks));
    return start <= utcTicks ? start : UtcStartOfLocalDay(LocalDay(utcTicks) - TimeSpan.TicksPerDay);
  }
}

/// <summary>
/// Caches the most recent local day lookup. Not thread-safe; use one per thread.
/// </summary>
sealed class LocalDayCache
{
  readonly ReportingCalendar calendar;

  long cachedStart = long.MaxValue;

  long cachedEnd = long.MinValue;

  long cachedDay;

  public LocalDayCache(ReportingCalendar calendar)
  {
    this.calendar = calendar;
  }

  public long LocalDay(long utcTicks)
  {
    if (utcTicks >= cachedStart && utcTicks < cachedEnd)
      return cachedDay;
    var day = calendar.LocalDay(utcTicks);
    cachedDay = day;
    cachedStart = calendar.UtcStartOfLocalDay(day);
    cachedEnd = calendar.UtcStartOfLocalDay(day + TimeSpan.TicksPerDay);
    if (utcTicks < cachedStart || utcTicks >= cachedEnd)
    {
      // Defensive: never cache an inconsistent range.
      cachedStart = long.MaxValue;
      cachedEnd = long.MinValue;
    }
    return day;
  }
}
