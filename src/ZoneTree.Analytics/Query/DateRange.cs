using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics;

/// <summary>
/// A time range: absolute, rolling, or a calendar range resolved in the query's time zone.
/// </summary>
public readonly struct DateRange : IEquatable<DateRange>
{
  enum Kind : byte
  {
    Between,
    Rolling,
    Days,
    Month,
    Year,
  }

  readonly Kind kind;

  readonly DateTimeOffset from;

  readonly DateTimeOffset to;

  readonly TimeSpan duration;

  readonly DateOnly firstDay;

  readonly DateOnly lastDay;

  readonly int relativeStart;

  readonly int relativeEnd;

  readonly bool isRelative;

  DateRange(
      Kind kind,
      DateTimeOffset from = default,
      DateTimeOffset to = default,
      TimeSpan duration = default,
      DateOnly firstDay = default,
      DateOnly lastDay = default,
      int relativeStart = 0,
      int relativeEnd = 0,
      bool isRelative = false)
  {
    this.kind = kind;
    this.from = from;
    this.to = to;
    this.duration = duration;
    this.firstDay = firstDay;
    this.lastDay = lastDay;
    this.relativeStart = relativeStart;
    this.relativeEnd = relativeEnd;
    this.isRelative = isRelative;
  }

  /// <summary>
  /// The absolute range [from, to).
  /// </summary>
  public static DateRange Between(DateTimeOffset from, DateTimeOffset to)
  {
    if (from >= to)
      throw new ArgumentException("The range start must be earlier than its end.", nameof(from));
    return new(Kind.Between, from, to);
  }

  /// <summary>
  /// The rolling range that ends now, for example the last 30 minutes.
  /// </summary>
  public static DateRange Last(TimeSpan duration)
  {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
    return new(Kind.Rolling, duration: duration);
  }

  /// <summary>
  /// Today in the query's time zone.
  /// </summary>
  public static DateRange Today => new(Kind.Days, relativeStart: 0, relativeEnd: 0, isRelative: true);

  /// <summary>
  /// Yesterday in the query's time zone.
  /// </summary>
  public static DateRange Yesterday => new(Kind.Days, relativeStart: -1, relativeEnd: -1, isRelative: true);

  /// <summary>
  /// The last <paramref name="days"/> calendar days, including today unless
  /// <paramref name="includeToday"/> is false.
  /// </summary>
  public static DateRange LastDays(int days, bool includeToday = true)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
    var end = includeToday ? 0 : -1;
    return new(Kind.Days, relativeStart: end - days + 1, relativeEnd: end, isRelative: true);
  }

  /// <summary>
  /// The current calendar month.
  /// </summary>
  public static DateRange ThisMonth => new(Kind.Month, relativeStart: 0, isRelative: true);

  /// <summary>
  /// The previous calendar month.
  /// </summary>
  public static DateRange LastMonth => new(Kind.Month, relativeStart: -1, isRelative: true);

  /// <summary>
  /// The current calendar year.
  /// </summary>
  public static DateRange ThisYear => new(Kind.Year, relativeStart: 0, isRelative: true);

  /// <summary>
  /// One calendar day.
  /// </summary>
  public static DateRange Day(DateOnly day) => new(Kind.Days, firstDay: day, lastDay: day);

  /// <summary>
  /// The calendar days from <paramref name="first"/> to <paramref name="last"/>, inclusive.
  /// </summary>
  public static DateRange Days(DateOnly first, DateOnly last)
  {
    if (last < first)
      throw new ArgumentException("The last day must not be earlier than the first day.", nameof(last));
    return new(Kind.Days, firstDay: first, lastDay: last);
  }

  /// <summary>
  /// One calendar month.
  /// </summary>
  public static DateRange Month(int year, int month) =>
      new(Kind.Month, firstDay: new DateOnly(year, month, 1));

  /// <summary>
  /// One calendar year.
  /// </summary>
  public static DateRange Year(int year) => new(Kind.Year, firstDay: new DateOnly(year, 1, 1));

  /// <summary>
  /// Resolves the range to UTC ticks [from, to) in the given calendar.
  /// </summary>
  internal (long From, long To) Resolve(ReportingCalendar calendar, long nowUtcTicks)
  {
    switch (kind)
    {
      case Kind.Between:
        return (from.UtcTicks, to.UtcTicks);
      case Kind.Rolling:
        return (nowUtcTicks - duration.Ticks, nowUtcTicks);
    }
    var (first, last) = ResolveDays(calendar, nowUtcTicks);
    return (
        calendar.UtcStartOfLocalDay(first.ToDateTime(TimeOnly.MinValue).Ticks),
        calendar.UtcStartOfLocalDay(last.AddDays(1).ToDateTime(TimeOnly.MinValue).Ticks));
  }

  (DateOnly First, DateOnly Last) ResolveDays(ReportingCalendar calendar, long nowUtcTicks)
  {
    var today = DateOnly.FromDateTime(new DateTime(calendar.LocalDay(nowUtcTicks)));
    switch (kind)
    {
      case Kind.Days:
        return isRelative
            ? (today.AddDays(relativeStart), today.AddDays(relativeEnd))
            : (firstDay, lastDay);
      case Kind.Month:
        {
          var start = isRelative
              ? new DateOnly(today.Year, today.Month, 1).AddMonths(relativeStart)
              : firstDay;
          return (start, start.AddMonths(1).AddDays(-1));
        }
      default:
        {
          var start = isRelative ? new DateOnly(today.Year + relativeStart, 1, 1) : firstDay;
          return (start, start.AddYears(1).AddDays(-1));
        }
    }
  }

  /// <summary>
  /// The period right before this one, of the same calendar shape: the previous day, the
  /// previous N days, the previous month or year, or the same duration before.
  /// </summary>
  internal DateRange PreviousPeriod(ReportingCalendar calendar, long nowUtcTicks)
  {
    switch (kind)
    {
      case Kind.Between:
        return new(Kind.Between, from - (to - from), from);
      case Kind.Rolling:
        {
          var end = new DateTimeOffset(nowUtcTicks - duration.Ticks, TimeSpan.Zero);
          return new(Kind.Between, end - duration, end);
        }
      case Kind.Month:
        {
          var (first, _) = ResolveDays(calendar, nowUtcTicks);
          return Month(first.AddMonths(-1).Year, first.AddMonths(-1).Month);
        }
      case Kind.Year:
        {
          var (first, _) = ResolveDays(calendar, nowUtcTicks);
          return Year(first.Year - 1);
        }
      default:
        {
          var (first, last) = ResolveDays(calendar, nowUtcTicks);
          var days = last.DayNumber - first.DayNumber + 1;
          return Days(first.AddDays(-days), last.AddDays(-days));
        }
    }
  }

  /// <summary>
  /// The same period one year earlier.
  /// </summary>
  internal DateRange PreviousYear(ReportingCalendar calendar, long nowUtcTicks)
  {
    switch (kind)
    {
      case Kind.Between:
        return new(Kind.Between, from.AddYears(-1), to.AddYears(-1));
      case Kind.Rolling:
        {
          var end = new DateTimeOffset(nowUtcTicks, TimeSpan.Zero).AddYears(-1);
          return new(Kind.Between, end - duration, end);
        }
      default:
        {
          var (first, last) = ResolveDays(calendar, nowUtcTicks);
          return Days(first.AddYears(-1), last.AddYears(-1));
        }
    }
  }

  /// <inheritdoc/>
  public bool Equals(DateRange other) =>
      kind == other.kind && from == other.from && to == other.to && duration == other.duration &&
      firstDay == other.firstDay && lastDay == other.lastDay && relativeStart == other.relativeStart &&
      relativeEnd == other.relativeEnd && isRelative == other.isRelative;

  /// <inheritdoc/>
  public override bool Equals(object obj) => obj is DateRange other && Equals(other);

  /// <inheritdoc/>
  public override int GetHashCode() =>
      HashCode.Combine(kind, from, to, duration, firstDay, lastDay, relativeStart, relativeEnd);

  /// <summary>Equality.</summary>
  public static bool operator ==(DateRange left, DateRange right) => left.Equals(right);

  /// <summary>Inequality.</summary>
  public static bool operator !=(DateRange left, DateRange right) => !left.Equals(right);

  /// <inheritdoc/>
  public override string ToString() => kind switch
  {
    Kind.Between => $"{from:o}..{to:o}",
    Kind.Rolling => $"last {duration}",
    Kind.Days when isRelative => $"days {relativeStart}..{relativeEnd} relative to today",
    Kind.Days => $"{firstDay:yyyy-MM-dd}..{lastDay:yyyy-MM-dd}",
    Kind.Month when isRelative => $"month {relativeStart} relative to this month",
    Kind.Month => $"{firstDay:yyyy-MM}",
    _ when isRelative => $"year {relativeStart} relative to this year",
    _ => $"{firstDay:yyyy}"
  };
}
