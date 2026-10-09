namespace ZoneTree.Analytics;

/// <summary>
/// Where a query reads its data from.
/// </summary>
public enum QuerySource
{
  /// <summary>
  /// Rollups where a rollup covers the query's dimensions, raw events otherwise.
  /// Ranges that rollups cannot cover exactly are completed from raw events.
  /// </summary>
  Auto,

  /// <summary>Only rollups.</summary>
  Rollups,

  /// <summary>Only raw events.</summary>
  RawEvents,
}

/// <summary>
/// An analytics query.
/// </summary>
/// <remarks>
/// <para>
/// A query aggregates a base <see cref="Event"/> over a <see cref="Range"/>, optionally as a
/// time series, grouped by dimensions and filtered. <see cref="Metrics"/> select what to
/// compute. Metrics may read other events than the base event: session metrics read sessions,
/// <see cref="Metric.ConversionRate"/> reads its goal event, and so on. The engine runs one
/// sub-query per event and joins them on the time bucket and group values.
/// </para>
/// <para>
/// A dimension that an event does not carry groups under <c>null</c>, so grouped totals always
/// equal ungrouped totals.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var result = await analytics.QueryAsync(new AnalyticsQuery
/// {
///     Range = DateRange.LastDays(30),
///     GroupBy = { StandardDimensions.Source },
///     Metrics = { Metric.Visitors, Metric.BounceRate, Metric.ConversionRate("signup") },
///     OrderBy = { Order.By(Metric.Visitors) },
///     Limit = 10,
/// });
/// </code>
/// </example>
public sealed class AnalyticsQuery
{
  /// <summary>
  /// The site. Defaults to <see cref="AnalyticsOptions.DefaultSite"/>.
  /// </summary>
  public string Site { get; set; }

  /// <summary>
  /// The base event. Default is <see cref="StandardEvents.PageView"/>.
  /// <see cref="Metric.Events"/>, <see cref="Metric.Visitors"/> and value metrics such as
  /// <see cref="Metric.Sum(string, string)"/> read this event.
  /// </summary>
  public string Event { get; set; } = StandardEvents.PageView;

  /// <summary>
  /// The time range. Default is the last 30 days including today.
  /// </summary>
  public DateRange Range { get; set; } = DateRange.LastDays(30);

  /// <summary>
  /// The time zone of calendar ranges and day, week, month and year buckets.
  /// Defaults to the engine's reporting time zone. Other time zones are answered from hourly
  /// rollups (and minute rollups or raw events for offsets that are not whole hours).
  /// </summary>
  public TimeZoneInfo TimeZone { get; set; }

  /// <summary>
  /// Time series interval. <see cref="TimeInterval.None"/> returns one bucket for the range.
  /// </summary>
  public TimeInterval Interval { get; set; }

  /// <summary>
  /// Dimensions to group by.
  /// </summary>
  public List<string> GroupBy { get; } = [];

  /// <summary>
  /// Filters on the rows of every sub-query. All filters must match.
  /// </summary>
  public List<Filter> Filters { get; } = [];

  /// <summary>
  /// Behavioral segments: only visitors or sessions that did (or did not do) something are
  /// counted. Segments are evaluated over raw events.
  /// </summary>
  public List<Segment> Segments { get; } = [];

  /// <summary>
  /// The metrics to compute. Default is <see cref="Metric.Events"/> and <see cref="Metric.Visitors"/>.
  /// </summary>
  public List<Metric> Metrics { get; } = [];

  /// <summary>
  /// Sort keys, applied in order. Default is bucket order for time series, otherwise the first
  /// metric descending.
  /// </summary>
  public List<Order> OrderBy { get; } = [];

  /// <summary>
  /// Maximum number of rows. Null returns all rows.
  /// </summary>
  public int? Limit { get; set; }

  /// <summary>
  /// Number of rows to skip, for paging.
  /// </summary>
  public int Offset { get; set; }

  /// <summary>
  /// Adds rows for empty buckets of a time series without group-by dimensions. Default is true.
  /// </summary>
  public bool FillEmptyBuckets { get; set; } = true;

  /// <summary>
  /// When true (the default), a row is returned for every bucket and group that has data for
  /// any metric, like a full outer join. When false, only buckets and groups that have base
  /// events are returned and the other metrics are joined onto them, like a left join: for
  /// example a page-view report that should not list sources with conversions but no page views.
  /// </summary>
  public bool IncludeRowsWithoutBaseEvent { get; set; } = true;

  /// <summary>
  /// Compares every row with a previous period. The result's rows carry
  /// <see cref="AnalyticsRow.Previous"/>.
  /// </summary>
  public Comparison Compare { get; set; }

  /// <summary>
  /// Data source selection. Default is <see cref="QuerySource.Auto"/>.
  /// </summary>
  public QuerySource Source { get; set; }

  internal AnalyticsQuery CloneForRange(DateRange range)
  {
    var clone = new AnalyticsQuery
    {
      Site = Site,
      Event = Event,
      Range = range,
      TimeZone = TimeZone,
      Interval = Interval,
      Limit = null,
      Offset = 0,
      FillEmptyBuckets = FillEmptyBuckets,
      IncludeRowsWithoutBaseEvent = IncludeRowsWithoutBaseEvent,
      Compare = null,
      Source = Source
    };
    clone.GroupBy.AddRange(GroupBy);
    clone.Filters.AddRange(Filters);
    clone.Segments.AddRange(Segments);
    clone.Metrics.AddRange(Metrics);
    return clone;
  }
}

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
    if (duration <= TimeSpan.Zero)
      throw new ArgumentOutOfRangeException(nameof(duration));
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
    if (days < 1)
      throw new ArgumentOutOfRangeException(nameof(days));
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
  internal (long From, long To) Resolve(Internal.ReportingCalendar calendar, long nowUtcTicks)
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

  (DateOnly First, DateOnly Last) ResolveDays(Internal.ReportingCalendar calendar, long nowUtcTicks)
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
  internal DateRange PreviousPeriod(Internal.ReportingCalendar calendar, long nowUtcTicks)
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
  internal DateRange PreviousYear(Internal.ReportingCalendar calendar, long nowUtcTicks)
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

/// <summary>
/// The period a query is compared with.
/// </summary>
public sealed class Comparison
{
  enum Kind
  {
    PreviousPeriod,
    PreviousYear,
    Custom,
  }

  readonly Kind kind;

  readonly DateRange range;

  Comparison(Kind kind, DateRange range = default)
  {
    this.kind = kind;
    this.range = range;
  }

  /// <summary>
  /// The period right before the query range, of the same shape: yesterday for today, the
  /// previous 30 days for the last 30 days, the previous month for a month.
  /// </summary>
  public static Comparison PreviousPeriod { get; } = new(Kind.PreviousPeriod);

  /// <summary>
  /// The same period one year earlier.
  /// </summary>
  public static Comparison PreviousYear { get; } = new(Kind.PreviousYear);

  /// <summary>
  /// An explicit range.
  /// </summary>
  public static Comparison Custom(DateRange range) => new(Kind.Custom, range);

  internal DateRange Resolve(DateRange current, Internal.ReportingCalendar calendar, long nowUtcTicks) => kind switch
  {
    Kind.PreviousPeriod => current.PreviousPeriod(calendar, nowUtcTicks),
    Kind.PreviousYear => current.PreviousYear(calendar, nowUtcTicks),
    _ => range
  };
}

/// <summary>
/// Filter operators.
/// </summary>
public enum FilterOperator
{
  /// <summary>Equal to one of the values.</summary>
  Equals,

  /// <summary>Not equal to any of the values. A missing dimension matches.</summary>
  NotEquals,

  /// <summary>Contains one of the values.</summary>
  Contains,

  /// <summary>Contains none of the values. A missing dimension matches.</summary>
  NotContains,

  /// <summary>Starts with one of the values.</summary>
  StartsWith,

  /// <summary>Ends with one of the values.</summary>
  EndsWith,

  /// <summary>Matches one of the regular expressions.</summary>
  Matches,

  /// <summary>Matches none of the regular expressions. A missing dimension matches.</summary>
  NotMatches,

  /// <summary>The event carries the dimension.</summary>
  IsSet,

  /// <summary>The event does not carry the dimension.</summary>
  IsNotSet,
}

/// <summary>
/// A condition on one dimension.
/// </summary>
/// <remarks>
/// A dimension that an event does not carry is "missing": positive operators never match it,
/// negative operators (<see cref="FilterOperator.NotEquals"/>, <see cref="FilterOperator.NotContains"/>,
/// <see cref="FilterOperator.NotMatches"/>) always do, and <see cref="FilterOperator.IsNotSet"/>
/// selects exactly the missing ones. An empty string is a known empty value, for example the
/// referrer of direct traffic.
/// </remarks>
public sealed class Filter
{
  static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

  readonly System.Text.RegularExpressions.Regex[] patterns;

  /// <summary>
  /// Creates a filter.
  /// </summary>
  public Filter(string dimension, FilterOperator op, params string[] values)
      : this(dimension, op, false, values)
  {
  }

  /// <summary>
  /// Creates a filter with optional case-insensitive comparison.
  /// </summary>
  public Filter(string dimension, FilterOperator op, bool ignoreCase, params string[] values)
  {
    if (string.IsNullOrWhiteSpace(dimension))
      throw new ArgumentException("The filter dimension is required.", nameof(dimension));
    values ??= [];
    var needsValues = op is not (FilterOperator.IsSet or FilterOperator.IsNotSet);
    if (needsValues && values.Length == 0)
      throw new ArgumentException("At least one filter value is required.", nameof(values));
    if (values.Any(x => x == null))
      throw new ArgumentException("Filter values cannot be null; use IsNotSet for missing values.", nameof(values));
    Dimension = dimension.Trim();
    Operator = op;
    IgnoreCase = ignoreCase;
    Values = needsValues ? values.ToArray() : [];
    if (op is FilterOperator.Matches or FilterOperator.NotMatches)
    {
      var options = System.Text.RegularExpressions.RegexOptions.CultureInvariant;
      if (ignoreCase)
        options |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
      patterns = Values
          .Select(x => new System.Text.RegularExpressions.Regex(x, options, RegexTimeout))
          .ToArray();
    }
  }

  /// <summary>The dimension.</summary>
  public string Dimension { get; }

  /// <summary>The operator.</summary>
  public FilterOperator Operator { get; }

  /// <summary>The values or patterns. A value matches when it matches any of them.</summary>
  public IReadOnlyList<string> Values { get; }

  /// <summary>True for case-insensitive comparison.</summary>
  public bool IgnoreCase { get; }

  /// <summary>Equal to one of the values.</summary>
  public static Filter Equal(string dimension, params string[] values) => new(dimension, FilterOperator.Equals, values);

  /// <summary>Not equal to any of the values.</summary>
  public static Filter NotEqual(string dimension, params string[] values) => new(dimension, FilterOperator.NotEquals, values);

  /// <summary>Contains one of the values.</summary>
  public static Filter Contains(string dimension, params string[] values) => new(dimension, FilterOperator.Contains, values);

  /// <summary>Contains none of the values.</summary>
  public static Filter NotContains(string dimension, params string[] values) => new(dimension, FilterOperator.NotContains, values);

  /// <summary>Starts with one of the values.</summary>
  public static Filter StartsWith(string dimension, params string[] values) => new(dimension, FilterOperator.StartsWith, values);

  /// <summary>Ends with one of the values.</summary>
  public static Filter EndsWith(string dimension, params string[] values) => new(dimension, FilterOperator.EndsWith, values);

  /// <summary>Matches one of the regular expressions.</summary>
  public static Filter Matches(string dimension, params string[] patterns) => new(dimension, FilterOperator.Matches, patterns);

  /// <summary>Matches none of the regular expressions.</summary>
  public static Filter NotMatches(string dimension, params string[] patterns) => new(dimension, FilterOperator.NotMatches, patterns);

  /// <summary>The event carries the dimension.</summary>
  public static Filter IsSet(string dimension) => new(dimension, FilterOperator.IsSet);

  /// <summary>The event does not carry the dimension.</summary>
  public static Filter IsNotSet(string dimension) => new(dimension, FilterOperator.IsNotSet);

  /// <summary>
  /// Evaluates the filter. <paramref name="value"/> is null when the dimension is missing.
  /// </summary>
  public bool IsMatch(string value)
  {
    var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    switch (Operator)
    {
      case FilterOperator.IsSet:
        return value != null;
      case FilterOperator.IsNotSet:
        return value == null;
      case FilterOperator.Equals:
        return value != null && Values.Any(x => string.Equals(value, x, comparison));
      case FilterOperator.NotEquals:
        return value == null || !Values.Any(x => string.Equals(value, x, comparison));
      case FilterOperator.Contains:
        return value != null && Values.Any(x => value.Contains(x, comparison));
      case FilterOperator.NotContains:
        return value == null || !Values.Any(x => value.Contains(x, comparison));
      case FilterOperator.StartsWith:
        return value != null && Values.Any(x => value.StartsWith(x, comparison));
      case FilterOperator.EndsWith:
        return value != null && Values.Any(x => value.EndsWith(x, comparison));
      case FilterOperator.Matches:
        return value != null && patterns.Any(x => x.IsMatch(value));
      case FilterOperator.NotMatches:
        return value == null || !patterns.Any(x => x.IsMatch(value));
      default:
        return false;
    }
  }

  /// <summary>
  /// Parses a filter expression:
  /// <c>dim==a|b</c>, <c>dim!=a</c>, <c>dim~=part</c> (contains), <c>dim!~=part</c>,
  /// <c>dim^=prefix</c>, <c>dim$=suffix</c>, <c>dim=~regex</c>, <c>dim!=~regex</c>,
  /// <c>dim?</c> (is set) and <c>!dim?</c> (is not set). Alternative values are separated by
  /// <c>|</c>; write a literal <c>|</c> as <c>\|</c> and a literal <c>\</c> before it as <c>\\</c>
  /// (other backslashes, for example in regular expressions, are kept as is). A leading <c>/i</c>
  /// after the operator, for example <c>page=~/i^/blog</c>, makes the comparison case-insensitive.
  /// </summary>
  public static Filter Parse(string expression)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(expression);
    expression = expression.Trim();
    (string Token, FilterOperator Op)[] operators =
    [
      ("!=~", FilterOperator.NotMatches),
      ("!~=", FilterOperator.NotContains),
      ("==", FilterOperator.Equals),
      ("!=", FilterOperator.NotEquals),
      ("~=", FilterOperator.Contains),
      ("^=", FilterOperator.StartsWith),
      ("$=", FilterOperator.EndsWith),
      ("=~", FilterOperator.Matches),
    ];
    var best = -1;
    var bestToken = default((string Token, FilterOperator Op));
    foreach (var candidate in operators)
    {
      var index = expression.IndexOf(candidate.Token, StringComparison.Ordinal);
      if (index > 0 && (best < 0 || index < best || (index == best && candidate.Token.Length > bestToken.Token.Length)))
      {
        best = index;
        bestToken = candidate;
      }
    }
    if (best < 0)
    {
      if (expression.Length > 1 && expression.EndsWith('?'))
      {
        var negated = expression.StartsWith('!');
        var name = expression[(negated ? 1 : 0)..^1];
        return new Filter(name, negated ? FilterOperator.IsNotSet : FilterOperator.IsSet);
      }
      throw new FormatException($"'{expression}' is not a filter expression.");
    }
    var dimension = expression[..best];
    var rest = expression[(best + bestToken.Token.Length)..];
    var ignoreCase = rest.StartsWith("/i", StringComparison.Ordinal);
    if (ignoreCase)
      rest = rest[2..];
    return new Filter(dimension, bestToken.Op, ignoreCase, SplitValues(rest));
  }

  static string[] SplitValues(string text)
  {
    var values = new List<string>();
    var current = new System.Text.StringBuilder();
    for (var i = 0; i < text.Length; ++i)
    {
      var c = text[i];
      // Only \| and \\ are escapes, so regular expressions keep their backslashes.
      if (c == '\\' && i + 1 < text.Length && text[i + 1] is '|' or '\\')
      {
        current.Append(text[++i]);
        continue;
      }
      if (c == '|')
      {
        values.Add(current.ToString());
        current.Clear();
        continue;
      }
      current.Append(c);
    }
    values.Add(current.ToString());
    return [.. values];
  }

  /// <inheritdoc/>
  public override string ToString()
  {
    var token = Operator switch
    {
      FilterOperator.Equals => "==",
      FilterOperator.NotEquals => "!=",
      FilterOperator.Contains => "~=",
      FilterOperator.NotContains => "!~=",
      FilterOperator.StartsWith => "^=",
      FilterOperator.EndsWith => "$=",
      FilterOperator.Matches => "=~",
      FilterOperator.NotMatches => "!=~",
      FilterOperator.IsSet => null,
      _ => null
    };
    if (Operator == FilterOperator.IsSet)
      return Dimension + "?";
    if (Operator == FilterOperator.IsNotSet)
      return "!" + Dimension + "?";
    var values = string.Join("|", Values.Select(x => x.Replace("\\", "\\\\").Replace("|", "\\|")));
    return Dimension + token + (IgnoreCase ? "/i" : "") + values;
  }
}

/// <summary>
/// Whether a segment selects visitors or sessions.
/// </summary>
public enum SegmentScope
{
  /// <summary>Visitors with at least one matching event in the range.</summary>
  Visitors,

  /// <summary>Sessions with at least one matching event.</summary>
  Sessions,
}

/// <summary>
/// A behavioral segment, for example "visitors who signed up" or "sessions that viewed /pricing".
/// </summary>
/// <remarks>
/// Segments restrict a query to the events of matching visitors or sessions. They require raw
/// events, so they are available within raw event retention.
/// </remarks>
public sealed class Segment
{
  Segment(SegmentScope scope, string eventName, Filter[] filters, bool isExcluded)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
    Scope = scope;
    Event = eventName;
    Filters = filters ?? [];
    IsExcluded = isExcluded;
  }

  /// <summary>Visitors or sessions.</summary>
  public SegmentScope Scope { get; }

  /// <summary>The event a visitor or session must have.</summary>
  public string Event { get; }

  /// <summary>Conditions on that event. All must match.</summary>
  public IReadOnlyList<Filter> Filters { get; }

  /// <summary>True when the segment selects visitors or sessions without such an event.</summary>
  public bool IsExcluded { get; }

  /// <summary>
  /// Visitors who had an event matching the filters in the query range.
  /// </summary>
  public static Segment VisitorsWho(string eventName, params Filter[] filters) =>
      new(SegmentScope.Visitors, eventName, filters, false);

  /// <summary>
  /// Sessions that contain an event matching the filters.
  /// </summary>
  public static Segment SessionsWith(string eventName, params Filter[] filters) =>
      new(SegmentScope.Sessions, eventName, filters, false);

  /// <summary>
  /// Visitors who viewed a page.
  /// </summary>
  public static Segment VisitorsWhoViewed(string page) =>
      VisitorsWho(StandardEvents.PageView, Filter.Equal(StandardDimensions.Page, page));

  /// <summary>
  /// The complement: visitors or sessions without a matching event.
  /// </summary>
  public Segment Exclude() => new(Scope, Event, [.. Filters], !IsExcluded);

  internal bool Matches(string eventName, Func<string, string> dimension)
  {
    if (eventName != Event)
      return false;
    foreach (var filter in Filters)
    {
      if (!filter.IsMatch(dimension(filter.Dimension)))
        return false;
    }
    return true;
  }

  /// <inheritdoc/>
  public override string ToString()
  {
    var prefix = IsExcluded ? "not " : "";
    var conditions = Filters.Count == 0 ? "" : " where " + string.Join(" and ", Filters);
    return $"{prefix}{Scope.ToString().ToLowerInvariant()} with {Event}{conditions}";
  }
}

/// <summary>
/// A sort key.
/// </summary>
public sealed class Order
{
  Order(Metric metric, string dimension, bool isBucket, bool descending)
  {
    Metric = metric;
    Dimension = dimension;
    IsBucket = isBucket;
    Descending = descending;
  }

  /// <summary>The metric, for metric sort keys.</summary>
  public Metric Metric { get; }

  /// <summary>The dimension, for dimension sort keys.</summary>
  public string Dimension { get; }

  /// <summary>True for the time bucket sort key.</summary>
  public bool IsBucket { get; }

  /// <summary>True for descending order.</summary>
  public bool Descending { get; }

  /// <summary>By a metric, descending by default.</summary>
  public static Order By(Metric metric, bool descending = true)
  {
    ArgumentNullException.ThrowIfNull(metric);
    return new(metric, null, false, descending);
  }

  /// <summary>By a group-by dimension, ascending by default. Missing values sort first.</summary>
  public static Order ByDimension(string dimension, bool descending = false)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
    return new(null, dimension, false, descending);
  }

  /// <summary>By time bucket, ascending by default.</summary>
  public static Order ByBucket(bool descending = false) => new(null, null, true, descending);

  /// <inheritdoc/>
  public override string ToString()
  {
    var key = Metric?.Key ?? Dimension ?? "bucket";
    return Descending ? "-" + key : key;
  }
}
