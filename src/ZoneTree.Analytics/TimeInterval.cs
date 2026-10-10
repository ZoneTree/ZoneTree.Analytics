using System.Globalization;

namespace ZoneTree.Analytics;

/// <summary>
/// The unit of a <see cref="TimeInterval"/>.
/// </summary>
public enum TimeIntervalUnit
{
  /// <summary>No time series.</summary>
  None,

  /// <summary>Minutes, in UTC.</summary>
  Minute,

  /// <summary>Hours, in UTC.</summary>
  Hour,

  /// <summary>Calendar days in the query's time zone.</summary>
  Day,

  /// <summary>Calendar weeks in the query's time zone.</summary>
  Week,

  /// <summary>Calendar months in the query's time zone.</summary>
  Month,

  /// <summary>Calendar years in the query's time zone.</summary>
  Year,
}

/// <summary>
/// The bucket size of a time series.
/// </summary>
/// <remarks>
/// Minute and hour buckets are UTC instants of a fixed width. <see cref="Minutes"/> and
/// <see cref="Hours"/> create wider buckets, for example 5-minute or 6-hour buckets; their
/// width divides a day, so buckets align to UTC midnight. Day, week, month and year buckets are
/// calendar periods of the query's time zone. <c>default</c> is <see cref="None"/>.
/// </remarks>
public readonly struct TimeInterval : IEquatable<TimeInterval>
{
  TimeInterval(TimeIntervalUnit unit, int count)
  {
    Unit = unit;
    Count = count;
  }

  /// <summary>The unit.</summary>
  public TimeIntervalUnit Unit { get; }

  /// <summary>Units per bucket: the width of minute and hour buckets, 1 for calendar units, 0 for <see cref="None"/>.</summary>
  public int Count { get; }

  /// <summary>No time series. A single bucket covers the whole query range.</summary>
  public static TimeInterval None => default;

  /// <summary>One-minute UTC buckets.</summary>
  public static TimeInterval Minute { get; } = new(TimeIntervalUnit.Minute, 1);

  /// <summary>One-hour UTC buckets.</summary>
  public static TimeInterval Hour { get; } = new(TimeIntervalUnit.Hour, 1);

  /// <summary>Calendar days in the query's time zone.</summary>
  public static TimeInterval Day { get; } = new(TimeIntervalUnit.Day, 1);

  /// <summary>Calendar weeks in the query's time zone, starting on <see cref="AnalyticsOptions.FirstDayOfWeek"/>.</summary>
  public static TimeInterval Week { get; } = new(TimeIntervalUnit.Week, 1);

  /// <summary>Calendar months in the query's time zone.</summary>
  public static TimeInterval Month { get; } = new(TimeIntervalUnit.Month, 1);

  /// <summary>Calendar years in the query's time zone.</summary>
  public static TimeInterval Year { get; } = new(TimeIntervalUnit.Year, 1);

  /// <summary>
  /// UTC buckets of <paramref name="minutes"/> minutes, for example 5 or 15. The width must
  /// divide a day (1440 minutes) and be shorter than a day; multiples of 60 are hour buckets.
  /// </summary>
  public static TimeInterval Minutes(int minutes)
  {
    if (minutes < 1 || minutes >= 1440 || 1440 % minutes != 0)
      throw new ArgumentOutOfRangeException(
          nameof(minutes), "The minutes must divide a day (1440) and be less than 1440, for example 5, 15 or 30.");
    return minutes % 60 == 0 ? Hours(minutes / 60) : new(TimeIntervalUnit.Minute, minutes);
  }

  /// <summary>
  /// UTC buckets of <paramref name="hours"/> hours: 1, 2, 3, 4, 6, 8 or 12. Use <see cref="Day"/>
  /// for calendar days.
  /// </summary>
  public static TimeInterval Hours(int hours)
  {
    if (hours < 1 || hours >= 24 || 24 % hours != 0)
      throw new ArgumentOutOfRangeException(nameof(hours), "The hours must be 1, 2, 3, 4, 6, 8 or 12.");
    return new(TimeIntervalUnit.Hour, hours);
  }

  /// <summary>True for fixed-width UTC buckets: minutes and hours.</summary>
  internal bool IsFixedWidth => Unit is TimeIntervalUnit.Minute or TimeIntervalUnit.Hour;

  /// <summary>The width of fixed-width buckets, in ticks.</summary>
  internal long WidthTicks => Unit switch
  {
    TimeIntervalUnit.Minute => Count * TimeSpan.TicksPerMinute,
    TimeIntervalUnit.Hour => Count * TimeSpan.TicksPerHour,
    _ => throw new InvalidOperationException($"{this} buckets have no fixed width.")
  };

  /// <summary>
  /// Parses "none", "minute", "hour", "day", "week", "month", "year", or a width such as "5m",
  /// "15m" or "6h".
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
  /// <exception cref="FormatException">The text is not an interval.</exception>
  public static TimeInterval Parse(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    return TryParse(text, out var interval) ? interval : throw new FormatException($"'{text}' is not a time interval.");
  }

  /// <summary>
  /// Tries to parse an interval. See <see cref="Parse"/>.
  /// </summary>
  public static bool TryParse(string text, out TimeInterval interval)
  {
    interval = default;
    if (string.IsNullOrWhiteSpace(text))
      return false;
    var value = text.Trim().ToLowerInvariant();
    switch (value)
    {
      case "none":
        return true;
      case "minute":
        interval = Minute;
        return true;
      case "hour":
        interval = Hour;
        return true;
      case "day":
        interval = Day;
        return true;
      case "week":
        interval = Week;
        return true;
      case "month":
        interval = Month;
        return true;
      case "year":
        interval = Year;
        return true;
    }
    if (value.Length < 2 ||
        !int.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
      return false;
    try
    {
      switch (value[^1])
      {
        case 'm':
          interval = Minutes(count);
          return true;
        case 'h':
          interval = Hours(count);
          return true;
      }
    }
    catch (ArgumentOutOfRangeException)
    {
    }
    return false;
  }

  /// <summary>
  /// The text form that <see cref="Parse"/> accepts: "day", "minute", "5m", "6h" and so on.
  /// </summary>
  public override string ToString()
  {
    if (Count > 1)
      return Count.ToString(CultureInfo.InvariantCulture) + (Unit == TimeIntervalUnit.Minute ? "m" : "h");
    return Unit.ToString().ToLowerInvariant();
  }

  /// <inheritdoc/>
  public bool Equals(TimeInterval other) => Unit == other.Unit && Count == other.Count;

  /// <inheritdoc/>
  public override bool Equals(object obj) => obj is TimeInterval other && Equals(other);

  /// <inheritdoc/>
  public override int GetHashCode() => HashCode.Combine(Unit, Count);

  /// <summary>Equality.</summary>
  public static bool operator ==(TimeInterval left, TimeInterval right) => left.Equals(right);

  /// <summary>Inequality.</summary>
  public static bool operator !=(TimeInterval left, TimeInterval right) => !left.Equals(right);
}
