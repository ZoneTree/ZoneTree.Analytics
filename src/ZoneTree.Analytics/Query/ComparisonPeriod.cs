using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics;

/// <summary>
/// The period a query is compared with.
/// </summary>
public sealed class ComparisonPeriod
{
  enum Kind
  {
    PreviousPeriod,
    PreviousYear,
    Custom,
  }

  readonly Kind kind;

  readonly DateRange range;

  ComparisonPeriod(Kind kind, DateRange range = default)
  {
    this.kind = kind;
    this.range = range;
  }

  /// <summary>
  /// The period right before the query range, of the same shape: yesterday for today, the
  /// previous 30 days for the last 30 days, the previous month for a month.
  /// </summary>
  public static ComparisonPeriod PreviousPeriod { get; } = new(Kind.PreviousPeriod);

  /// <summary>
  /// The same period one year earlier.
  /// </summary>
  public static ComparisonPeriod PreviousYear { get; } = new(Kind.PreviousYear);

  /// <summary>
  /// An explicit range.
  /// </summary>
  public static ComparisonPeriod Custom(DateRange range) => new(Kind.Custom, range);

  /// <summary>
  /// The text form, which <see cref="Parse"/> reads back: <c>previous-period</c>,
  /// <c>previous-year</c>, or the text form of a custom range (see <see cref="DateRange.Parse"/>).
  /// </summary>
  public override string ToString() => kind switch
  {
    Kind.PreviousPeriod => "previous-period",
    Kind.PreviousYear => "previous-year",
    _ => range.ToString()
  };

  /// <summary>
  /// Parses <c>previous-period</c>, <c>previous-year</c>, or a date range for a
  /// custom period (see <see cref="DateRange.Parse"/>).
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
  /// <exception cref="FormatException">The text is not a comparison period.</exception>
  public static ComparisonPeriod Parse(string text) => Parsing.Parse(text, "comparison period", value =>
      value.ToLowerInvariant() switch
      {
        "previous-period" => PreviousPeriod,
        "previous-year" => PreviousYear,
        _ => Custom(DateRange.Parse(value))
      });

  /// <summary>
  /// Tries to parse a comparison period. See <see cref="Parse"/>.
  /// </summary>
  public static bool TryParse(string text, out ComparisonPeriod period) => Parsing.TryParse(text, Parse, out period);

  internal DateRange Resolve(DateRange current, ReportingCalendar calendar, long nowUtcTicks) => kind switch
  {
    Kind.PreviousPeriod => current.PreviousPeriod(calendar, nowUtcTicks),
    Kind.PreviousYear => current.PreviousYear(calendar, nowUtcTicks),
    _ => range
  };
}
