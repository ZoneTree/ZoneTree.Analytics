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

  internal DateRange Resolve(DateRange current, ReportingCalendar calendar, long nowUtcTicks) => kind switch
  {
    Kind.PreviousPeriod => current.PreviousPeriod(calendar, nowUtcTicks),
    Kind.PreviousYear => current.PreviousYear(calendar, nowUtcTicks),
    _ => range
  };
}
