using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using ZoneTree.Analytics.Internal;

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

  /// <summary>At least one child filter matches.</summary>
  AnyOf,

  /// <summary>Every child filter matches.</summary>
  AllOf,
}

/// <summary>
/// A condition on one dimension, or a group of conditions.
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
  // The comparison tokens are shared with MetricFilter; the operators of the same name match.
  static readonly (string Token, FilterOperator Operator)[] Tokens =
  [
    ("!=~", FilterOperator.NotMatches),
    ("!~=", FilterOperator.NotContains),
    ("~=", FilterOperator.Contains),
    ("^=", FilterOperator.StartsWith),
    ("$=", FilterOperator.EndsWith),
    ("=~", FilterOperator.Matches),
    .. ComparisonTokens.All.Select(x => (x.Token, Enum.Parse<FilterOperator>(x.Operator.ToString()))),
  ];

  readonly string[] values;

  readonly Regex[] patterns;

  readonly double number;

  readonly IReadOnlyList<Filter> children = Array.Empty<Filter>();

  readonly IReadOnlyList<string> dimensions;

  readonly int depth = 1;

  readonly int leafCount = 1;

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
    if (!Enum.IsDefined(op) || op is FilterOperator.AnyOf or FilterOperator.AllOf)
      throw new ArgumentException("Use AnyOf or AllOf to create filter groups.", nameof(op));
    if (string.IsNullOrWhiteSpace(dimension))
      throw new ArgumentException("The filter dimension is required.", nameof(dimension));
    Text.ValidateUnicode(dimension, nameof(dimension));
    values ??= [];
    var needsValues = op is not (FilterOperator.IsSet or FilterOperator.IsNotSet);
    if (needsValues && values.Length == 0)
      throw new ArgumentException("At least one filter value is required.", nameof(values));
    if (values.Any(x => x == null))
      throw new ArgumentException("Filter values cannot be null; use IsNotSet for missing values.", nameof(values));
    foreach (var value in values)
      Text.ValidateUnicode(value, nameof(values));
    Dimension = dimension.Trim();
    dimensions = Array.AsReadOnly(new[] { Dimension });
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

  Filter(FilterOperator op, Filter[] filters)
  {
    ArgumentNullException.ThrowIfNull(filters);
    if (filters.Length == 0 || filters.Length > 1024 || filters.Any(x => x == null))
      throw new ArgumentException("A filter group needs at least one non-null filter.", nameof(filters));
    depth = 1 + filters.Max(x => x.depth);
    leafCount = filters.Sum(x => x.leafCount);
    if (depth > 32 || leafCount > 1024)
      throw new ArgumentException("A filter group supports at most 32 levels and 1,024 conditions.", nameof(filters));
    Operator = op;
    children = Array.AsReadOnly((Filter[])filters.Clone());
    dimensions = Array.AsReadOnly(filters.SelectMany(x => x.Dimensions).Distinct(StringComparer.Ordinal).ToArray());
    values = [];
  }

  /// <summary>Matches when at least one child matches. Groups can be nested.</summary>
  public static Filter AnyOf(params Filter[] filters) => new(FilterOperator.AnyOf, filters);

  /// <summary>Matches when every child matches. Groups can be nested.</summary>
  public static Filter AllOf(params Filter[] filters) => new(FilterOperator.AllOf, filters);

  /// <summary>The child conditions of a group; empty for a single condition.</summary>
  public IReadOnlyList<Filter> Children => children;

  /// <summary>All dimensions referenced by this condition, including nested groups.</summary>
  public IReadOnlyList<string> Dimensions => dimensions;

  /// <summary>The dimension of a single condition; null for a group.</summary>
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
    if (children.Count > 0)
      throw new InvalidOperationException("Evaluate a filter group with Matches(dimensions).");
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

  /// <summary>Evaluates a condition or group against dimension values. Missing keys are null.</summary>
  public bool Matches(IReadOnlyDictionary<string, string> dimensions)
  {
    ArgumentNullException.ThrowIfNull(dimensions);
    return IsMatch(new FilterValues(dimensions));
  }

  internal bool IsMatch(in FilterValues dimensions)
  {
    if (children.Count == 0)
      return IsMatch(dimensions.Get(Dimension));
    var any = Operator == FilterOperator.AnyOf;
    foreach (var child in children)
    {
      if (child.IsMatch(dimensions) == any)
        return any;
    }
    return !any;
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
  /// <c>|</c>; write a literal <c>|</c> as <c>\|</c>, a literal <c>;</c> as <c>\;</c> (it separates
  /// the filters of a segment, see <see cref="Segment.Parse"/>) and a literal <c>\</c> before them as
  /// <c>\\</c> (other backslashes, for example in regular expressions, are kept as is). A leading
  /// <c>/i</c> after the operator, for example <c>page=~/i^/blog</c>, makes the comparison
  /// case-insensitive.
  /// Groups use <c>any("country==DE","page==/pricing")</c> or
  /// <c>all("country==DE",any("page==/","page==/pricing"))</c>. Single conditions in groups
  /// are JSON-quoted strings; nested groups can be written directly. Groups are limited to
  /// 32 levels and 1,024 single conditions. <see cref="ToString"/> produces this text form.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
  /// <exception cref="FormatException">The text is not a filter.</exception>
  public static Filter Parse(string expression) => Parsing.Parse(expression, "filter", text => ParseCore(text, 1));

  /// <summary>
  /// Tries to parse a filter expression. See <see cref="Parse"/>.
  /// </summary>
  public static bool TryParse(string expression, out Filter filter) => Parsing.TryParse(expression, Parse, out filter);

  static Filter ParseCore(string expression, int depth)
  {
    if (depth > 32)
      throw new FormatException("Filter groups support at most 32 levels.");
    if (expression.StartsWith("any(", StringComparison.Ordinal) || expression.StartsWith("all(", StringComparison.Ordinal))
    {
      if (!expression.EndsWith(')'))
        throw new FormatException("The filter group is not closed.");
      try
      {
        var items = SplitGroupItems(expression[4..^1]).ToArray();
        if (items.Length > 1024)
          throw new FormatException("A filter group contains at most 1,024 quoted filter expressions.");
        var filters = items.Select(x =>
        {
          var text = x.Trim();
          if (text.StartsWith("any(", StringComparison.Ordinal) || text.StartsWith("all(", StringComparison.Ordinal))
            return ParseCore(text, depth + 1);
          using var document = JsonDocument.Parse(text);
          if (document.RootElement.ValueKind != JsonValueKind.String)
            throw new FormatException("A filter group contains quoted conditions or nested groups.");
          return ParseCore(document.RootElement.GetString().Trim(), depth + 1);
        }).ToArray();
        return expression.StartsWith("any(", StringComparison.Ordinal) ? AnyOf(filters) : AllOf(filters);
      }
      catch (Exception exception) when (exception is JsonException or InvalidOperationException)
      {
        throw new FormatException("Filter groups contain valid Unicode JSON-quoted filter expressions.", exception);
      }
    }
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

  static IEnumerable<string> SplitGroupItems(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
      yield break;
    var start = 0;
    var depth = 0;
    var quoted = false;
    for (var i = 0; i < text.Length; ++i)
    {
      var c = text[i];
      if (quoted && c == '\\' && i + 1 < text.Length)
      {
        ++i;
        continue;
      }
      if (c == '"')
        quoted = !quoted;
      if (quoted)
        continue;
      if (c == '(')
        ++depth;
      else if (c == ')')
        --depth;
      else if (c == ',' && depth == 0)
      {
        yield return text[start..i];
        start = i + 1;
      }
      if (depth < 0)
        throw new FormatException("Unbalanced filter group.");
    }
    if (quoted || depth != 0)
      throw new FormatException("Unbalanced filter group.");
    yield return text[start..];
  }

  static string[] SplitValues(string text)
  {
    var values = new List<string>();
    var current = new StringBuilder();
    for (var i = 0; i < text.Length; ++i)
    {
      var c = text[i];
      // Only \|, \; and \\ are escapes, so regular expressions keep their backslashes.
      if (c == '\\' && i + 1 < text.Length && text[i + 1] is '|' or ';' or '\\')
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
    if (children.Count > 0)
      return (Operator == FilterOperator.AnyOf ? "any(" : "all(") +
          string.Join(",", children.Select(x => x.Children.Count > 0 ? x.ToString() : "\"" + JsonEncodedText.Encode(x.ToString()) + "\"")) + ")";
    switch (Operator)
    {
      case FilterOperator.IsSet:
        return Dimension + "?";
      case FilterOperator.IsNotSet:
        return "!" + Dimension + "?";
    }
    var token = Tokens.First(x => x.Operator == Operator).Token;
    var values = string.Join("|", Values.Select(x => x.Replace("\\", "\\\\").Replace("|", "\\|").Replace(";", "\\;")));
    return Dimension + token + (IgnoreCase ? "/i" : "") + values;
  }
}
