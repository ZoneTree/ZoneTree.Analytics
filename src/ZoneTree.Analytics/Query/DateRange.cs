using System.Globalization;
using System.Text.RegularExpressions;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics;

/// <summary>
/// A time range: absolute, rolling, or a calendar range resolved in the query's time zone.
/// </summary>
/// <remarks>
/// Every range has a text form that <see cref="Parse"/> reads and <see cref="ToString"/> writes:
/// <c>today</c>, <c>yesterday</c>, <c>30d</c> (the last 30 days including today),
/// <c>today-7..today-1</c>, <c>12h</c>, <c>30m</c> or <c>45s</c> (rolling), <c>this-month</c>,
/// <c>last-month</c>, <c>this-year</c>, <c>last-year</c>, <c>2026</c>, <c>2026-02</c>,
/// <c>2026-02-28</c>, <c>2026-02-01..2026-02-28</c>, or two ISO 8601 date-times separated by
/// <c>..</c>.
/// </remarks>
public readonly struct DateRange : IEquatable<DateRange>
{
  static readonly Regex RelativePeriod = new(@"^this-(month|year)([+-]\d+)$", RegexOptions.CultureInvariant);

  static readonly Regex RelativeDay = new(@"^today([+-]\d+)?$", RegexOptions.CultureInvariant);

  static readonly Regex Duration = new(@"^(\d+)(d|h|m|s|ms)$", RegexOptions.CultureInvariant);

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
  /// The previous calendar year.
  /// </summary>
  public static DateRange LastYear => new(Kind.Year, relativeStart: -1, isRelative: true);

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

  /// <summary>
  /// The text form of the range, which <see cref="Parse"/> reads back.
  /// </summary>
  public override string ToString() => kind switch
  {
    Kind.Between => from.ToString("o", CultureInfo.InvariantCulture) + ".." + to.ToString("o", CultureInfo.InvariantCulture),
    Kind.Rolling => FormatDuration(duration),
    Kind.Days when isRelative => FormatRelativeDays(relativeStart, relativeEnd),
    Kind.Days when firstDay == lastDay => FormatDay(firstDay),
    Kind.Days => FormatDay(firstDay) + ".." + FormatDay(lastDay),
    Kind.Month when isRelative => FormatRelative("month", relativeStart),
    Kind.Month => firstDay.ToString("yyyy-MM", CultureInfo.InvariantCulture),
    _ when isRelative => FormatRelative("year", relativeStart),
    _ => firstDay.ToString("yyyy", CultureInfo.InvariantCulture)
  };

  static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  static string FormatOffset(int offset) => offset.ToString("+0;-0", CultureInfo.InvariantCulture);

  static string FormatRelativeDay(int offset) => offset == 0 ? "today" : "today" + FormatOffset(offset);

  static string FormatRelativeDays(int start, int end) => (start, end) switch
  {
    (0, 0) => "today",
    (-1, -1) => "yesterday",
    (_, 0) when start < 0 => (1 - start).ToString(CultureInfo.InvariantCulture) + "d",
    _ => FormatRelativeDay(start) + ".." + FormatRelativeDay(end)
  };

  static string FormatRelative(string unit, int offset) => offset switch
  {
    0 => "this-" + unit,
    -1 => "last-" + unit,
    _ => "this-" + unit + FormatOffset(offset)
  };

  static string FormatDuration(TimeSpan duration)
  {
    var ticks = duration.Ticks;
    if (ticks % TimeSpan.TicksPerHour == 0)
      return (ticks / TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture) + "h";
    if (ticks % TimeSpan.TicksPerMinute == 0)
      return (ticks / TimeSpan.TicksPerMinute).ToString(CultureInfo.InvariantCulture) + "m";
    if (ticks % TimeSpan.TicksPerSecond == 0)
      return (ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture) + "s";
    return duration.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture) + "ms";
  }

  /// <summary>
  /// Parses the text form of a range (see <see cref="DateRange"/>). Keywords are case-insensitive.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
  /// <exception cref="FormatException">The text is not a date range.</exception>
  public static DateRange Parse(string text) => Parsing.Parse(text, "date range", ParseCore);

  /// <summary>
  /// Tries to parse the text form of a range. See <see cref="Parse"/>.
  /// </summary>
  public static bool TryParse(string text, out DateRange range) => Parsing.TryParse(text, Parse, out range);

  static DateRange ParseCore(string text)
  {
    var keyword = text.ToLowerInvariant();
    switch (keyword)
    {
      case "today":
        return Today;
      case "yesterday":
        return Yesterday;
      case "this-month":
        return ThisMonth;
      case "last-month":
        return LastMonth;
      case "this-year":
        return ThisYear;
      case "last-year":
        return LastYear;
    }
    if (RelativePeriod.Match(keyword) is { Success: true } period)
    {
      var offset = int.Parse(period.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
      return new(period.Groups[1].Value == "month" ? Kind.Month : Kind.Year, relativeStart: offset, isRelative: true);
    }
    if (RelativeDay.Match(keyword) is { Success: true } day)
    {
      var offset = ParseDayOffset(day);
      return RelativeDays(offset, offset);
    }
    if (Duration.Match(keyword) is { Success: true } duration)
    {
      var count = long.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture);
      return duration.Groups[2].Value switch
      {
        "d" => LastDays(checked((int)count)),
        "h" => Last(TimeSpan.FromHours(count)),
        "m" => Last(TimeSpan.FromMinutes(count)),
        "s" => Last(TimeSpan.FromSeconds(count)),
        _ => Last(TimeSpan.FromMilliseconds(count))
      };
    }
    if (keyword.EndsWith("ms", StringComparison.Ordinal) &&
        double.TryParse(keyword[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
      return Last(TimeSpan.FromMilliseconds(milliseconds));
    var separator = text.IndexOf("..", StringComparison.Ordinal);
    if (separator > 0)
    {
      var first = text[..separator].Trim();
      var last = text[(separator + 2)..].Trim();
      if (RelativeDay.Match(first.ToLowerInvariant()) is { Success: true } start &&
          RelativeDay.Match(last.ToLowerInvariant()) is { Success: true } end)
        return RelativeDays(ParseDayOffset(start), ParseDayOffset(end));
      if (TryParseDay(first, out var firstDay) && TryParseDay(last, out var lastDay))
        return Days(firstDay, lastDay);
      if (DateTimeOffset.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from) &&
          DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to))
        return Between(from, to);
      throw new FormatException(
          $"'{text}' is not a date range: both ends must be days (yyyy-MM-dd), date-times (ISO 8601) or relative days (today-7).");
    }
    if (TryParseDay(text, out var single))
      return Day(single);
    if (DateOnly.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
      return Month(month.Year, month.Month);
    if (text.Length == 4 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
      return Year(year);
    throw new FormatException(
        $"'{text}' is not a date range. Use today, yesterday, 30d, 12h, 30m, this-month, last-month, this-year, " +
        "last-year, 2026, 2026-02, 2026-02-28, or two days or date-times separated by '..'.");
  }

  static bool TryParseDay(string text, out DateOnly day) =>
      DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

  static int ParseDayOffset(Match match) => match.Groups[1].Success
      ? int.Parse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
      : 0;

  static DateRange RelativeDays(int start, int end)
  {
    if (end < start)
      throw new ArgumentException("The last day must not be earlier than the first day.", nameof(end));
    return new(Kind.Days, relativeStart: start, relativeEnd: end, isRelative: true);
  }
}
