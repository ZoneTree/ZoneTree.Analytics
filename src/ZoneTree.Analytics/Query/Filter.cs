using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZoneTree.Analytics;

/// <summary>
/// Filter operators.
/// </summary>
public enum FilterOperator
{
  /// <summary>Equal to one of the values.</summary>
  Equal,

  /// <summary>Not equal to any of the values. A missing dimension matches.</summary>
  NotEqual,

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

  /// <summary>Numerically greater than the value. Values that are not numbers do not match.</summary>
  GreaterThan,

  /// <summary>Numerically greater than or equal to the value. Values that are not numbers do not match.</summary>
  GreaterThanOrEqual,

  /// <summary>Numerically less than the value. Values that are not numbers do not match.</summary>
  LessThan,

  /// <summary>Numerically less than or equal to the value. Values that are not numbers do not match.</summary>
  LessThanOrEqual,
}

/// <summary>
/// A condition on one dimension.
/// </summary>
/// <remarks>
/// A dimension that an event does not carry is "missing": positive operators never match it,
/// negative operators (<see cref="FilterOperator.NotEqual"/>, <see cref="FilterOperator.NotContains"/>,
/// <see cref="FilterOperator.NotMatches"/>) always do, and <see cref="FilterOperator.IsNotSet"/>
/// selects exactly the missing ones. An empty string is a known empty value, for example the
/// referrer of direct traffic.
/// </remarks>
public sealed class Filter
{
  static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

  // The expression syntax of Parse and ToString. Longer tokens come first among tokens that
  // share a prefix, so "!=~" is not read as "!=" followed by "~".
  static readonly (string Token, FilterOperator Operator)[] Tokens =
  [
    ("!=~", FilterOperator.NotMatches),
    ("!~=", FilterOperator.NotContains),
    ("==", FilterOperator.Equal),
    ("!=", FilterOperator.NotEqual),
    ("~=", FilterOperator.Contains),
    ("^=", FilterOperator.StartsWith),
    ("$=", FilterOperator.EndsWith),
    ("=~", FilterOperator.Matches),
    (">=", FilterOperator.GreaterThanOrEqual),
    ("<=", FilterOperator.LessThanOrEqual),
    (">", FilterOperator.GreaterThan),
    ("<", FilterOperator.LessThan),
  ];

  readonly string[] values;

  readonly Regex[] patterns;

  readonly double number;

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
    this.values = needsValues ? [.. values] : [];
    if (IsNumeric(op))
    {
      if (values.Length != 1 ||
          !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
          !double.IsFinite(number))
        throw new ArgumentException("A numeric filter needs exactly one number.", nameof(values));
    }
    if (op is FilterOperator.Matches or FilterOperator.NotMatches)
    {
      var options = RegexOptions.CultureInvariant;
      if (ignoreCase)
        options |= RegexOptions.IgnoreCase;
      patterns = Values
          .Select(x => new Regex(x, options, RegexTimeout))
          .ToArray();
    }
  }

  /// <summary>The dimension.</summary>
  public string Dimension { get; }

  /// <summary>The operator.</summary>
  public FilterOperator Operator { get; }

  /// <summary>The values or patterns. A value matches when it matches any of them.</summary>
  public IReadOnlyList<string> Values => values;

  /// <summary>True for case-insensitive comparison.</summary>
  public bool IgnoreCase { get; }

  /// <summary>Equal to one of the values.</summary>
  public static Filter Equal(string dimension, params string[] values) => new(dimension, FilterOperator.Equal, values);

  /// <summary>Not equal to any of the values.</summary>
  public static Filter NotEqual(string dimension, params string[] values) => new(dimension, FilterOperator.NotEqual, values);

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

  /// <summary>Numerically greater than the value.</summary>
  public static Filter GreaterThan(string dimension, double value) => Numeric(dimension, FilterOperator.GreaterThan, value);

  /// <summary>Numerically greater than or equal to the value.</summary>
  public static Filter GreaterThanOrEqual(string dimension, double value) =>
      Numeric(dimension, FilterOperator.GreaterThanOrEqual, value);

  /// <summary>Numerically less than the value.</summary>
  public static Filter LessThan(string dimension, double value) => Numeric(dimension, FilterOperator.LessThan, value);

  /// <summary>Numerically less than or equal to the value.</summary>
  public static Filter LessThanOrEqual(string dimension, double value) =>
      Numeric(dimension, FilterOperator.LessThanOrEqual, value);

  static Filter Numeric(string dimension, FilterOperator op, double value) =>
      new(dimension, op, value.ToString("R", CultureInfo.InvariantCulture));

  static bool IsNumeric(FilterOperator op) =>
      op is FilterOperator.GreaterThan or FilterOperator.GreaterThanOrEqual or
          FilterOperator.LessThan or FilterOperator.LessThanOrEqual;

  /// <summary>The event carries the dimension.</summary>
  public static Filter IsSet(string dimension) => new(dimension, FilterOperator.IsSet);

  /// <summary>The event does not carry the dimension.</summary>
  public static Filter IsNotSet(string dimension) => new(dimension, FilterOperator.IsNotSet);

  /// <summary>
  /// Evaluates the filter. <paramref name="value"/> is null when the dimension is missing.
  /// </summary>
  public bool IsMatch(string value)
  {
    // Called for every row a query tests: plain loops, no delegates or enumerators.
    switch (Operator)
    {
      case FilterOperator.IsSet:
        return value != null;
      case FilterOperator.IsNotSet:
        return value == null;
    }
    if (IsNumeric(Operator))
      return value != null && MatchesNumber(value);
    var negated = Operator is FilterOperator.NotEqual or FilterOperator.NotContains or FilterOperator.NotMatches;
    if (value == null)
      return negated;
    var matched = Operator is FilterOperator.Matches or FilterOperator.NotMatches
        ? MatchesAnyPattern(value)
        : MatchesAnyValue(value);
    return matched != negated;
  }

  bool MatchesAnyValue(string value)
  {
    var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    foreach (var candidate in values)
    {
      var matched = Operator switch
      {
        FilterOperator.Equal or FilterOperator.NotEqual => string.Equals(value, candidate, comparison),
        FilterOperator.Contains or FilterOperator.NotContains => value.Contains(candidate, comparison),
        FilterOperator.StartsWith => value.StartsWith(candidate, comparison),
        _ => value.EndsWith(candidate, comparison)
      };
      if (matched)
        return true;
    }
    return false;
  }

  bool MatchesNumber(string value)
  {
    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
      return false;
    return Operator switch
    {
      FilterOperator.GreaterThan => parsed > number,
      FilterOperator.GreaterThanOrEqual => parsed >= number,
      FilterOperator.LessThan => parsed < number,
      _ => parsed <= number
    };
  }

  bool MatchesAnyPattern(string value)
  {
    foreach (var pattern in patterns)
    {
      if (pattern.IsMatch(value))
        return true;
    }
    return false;
  }

  /// <summary>
  /// Parses a filter expression:
  /// <c>dim==a|b</c>, <c>dim!=a</c>, <c>dim~=part</c> (contains), <c>dim!~=part</c>,
  /// <c>dim^=prefix</c>, <c>dim$=suffix</c>, <c>dim=~regex</c>, <c>dim!=~regex</c>, numeric
  /// comparisons <c>dim&gt;n</c>, <c>dim&gt;=n</c>, <c>dim&lt;n</c> and <c>dim&lt;=n</c>,
  /// <c>dim?</c> (is set) and <c>!dim?</c> (is not set). Alternative values are separated by
  /// <c>|</c>; write a literal <c>|</c> as <c>\|</c> and a literal <c>\</c> before it as <c>\\</c>
  /// (other backslashes, for example in regular expressions, are kept as is). A leading <c>/i</c>
  /// after the operator, for example <c>page=~/i^/blog</c>, makes the comparison case-insensitive.
  /// </summary>
  public static Filter Parse(string expression)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(expression);
    expression = expression.Trim();
    // The operator is the earliest token in the expression; at the same position, the longest.
    var best = -1;
    var bestToken = default((string Token, FilterOperator Operator));
    foreach (var candidate in Tokens)
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
    return new Filter(dimension, bestToken.Operator, ignoreCase, SplitValues(rest));
  }

  static string[] SplitValues(string text)
  {
    var values = new List<string>();
    var current = new StringBuilder();
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
    switch (Operator)
    {
      case FilterOperator.IsSet:
        return Dimension + "?";
      case FilterOperator.IsNotSet:
        return "!" + Dimension + "?";
    }
    var token = Tokens.First(x => x.Operator == Operator).Token;
    var values = string.Join("|", Values.Select(x => x.Replace("\\", "\\\\").Replace("|", "\\|")));
    return Dimension + token + (IgnoreCase ? "/i" : "") + values;
  }
}
