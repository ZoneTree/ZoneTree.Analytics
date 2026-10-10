using ZoneTree.Analytics.Internal;
using System.Globalization;

namespace ZoneTree.Analytics;

/// <summary>
/// Comparison operators of a <see cref="MetricFilter"/>.
/// </summary>
public enum ComparisonOperator
{
  /// <summary>Equal to the value.</summary>
  Equal,

  /// <summary>Not equal to the value.</summary>
  NotEqual,

  /// <summary>Greater than the value.</summary>
  GreaterThan,

  /// <summary>Greater than or equal to the value.</summary>
  GreaterThanOrEqual,

  /// <summary>Less than the value.</summary>
  LessThan,

  /// <summary>Less than or equal to the value.</summary>
  LessThanOrEqual,
}

/// <summary>
/// A condition on the value of a metric in a result row, like SQL HAVING: for example pages
/// with more than 100 page views. See <see cref="AnalyticsQuery.Having"/>.
/// </summary>
public sealed class MetricFilter
{
  static readonly (string Token, ComparisonOperator Operator)[] Tokens = ComparisonTokens.All;

  /// <summary>
  /// Creates a condition.
  /// </summary>
  public MetricFilter(Metric metric, ComparisonOperator op, double value)
  {
    ArgumentNullException.ThrowIfNull(metric);
    if (!Enum.IsDefined(op))
      throw new ArgumentOutOfRangeException(nameof(op));
    if (!double.IsFinite(value))
      throw new ArgumentOutOfRangeException(nameof(value), "The value must be a finite number.");
    Metric = metric;
    Operator = op;
    Value = value;
  }

  /// <summary>The metric.</summary>
  public Metric Metric { get; }

  /// <summary>The operator.</summary>
  public ComparisonOperator Operator { get; }

  /// <summary>The value compared with.</summary>
  public double Value { get; }

  /// <summary>The metric is equal to the value.</summary>
  public static MetricFilter Equal(Metric metric, double value) => new(metric, ComparisonOperator.Equal, value);

  /// <summary>The metric is not equal to the value.</summary>
  public static MetricFilter NotEqual(Metric metric, double value) => new(metric, ComparisonOperator.NotEqual, value);

  /// <summary>The metric is greater than the value.</summary>
  public static MetricFilter GreaterThan(Metric metric, double value) => new(metric, ComparisonOperator.GreaterThan, value);

  /// <summary>The metric is greater than or equal to the value.</summary>
  public static MetricFilter GreaterThanOrEqual(Metric metric, double value) =>
      new(metric, ComparisonOperator.GreaterThanOrEqual, value);

  /// <summary>The metric is less than the value.</summary>
  public static MetricFilter LessThan(Metric metric, double value) => new(metric, ComparisonOperator.LessThan, value);

  /// <summary>The metric is less than or equal to the value.</summary>
  public static MetricFilter LessThanOrEqual(Metric metric, double value) =>
      new(metric, ComparisonOperator.LessThanOrEqual, value);

  internal bool IsMatch(double value) => Operator switch
  {
    ComparisonOperator.Equal => value == Value,
    ComparisonOperator.NotEqual => value != Value,
    ComparisonOperator.GreaterThan => value > Value,
    ComparisonOperator.GreaterThanOrEqual => value >= Value,
    ComparisonOperator.LessThan => value < Value,
    _ => value <= Value
  };

  /// <summary>
  /// Parses a condition: a metric key (see <see cref="Metric.Parse"/>), an operator (==, !=, &gt;,
  /// &gt;=, &lt; or &lt;=) and a number, for example "pageviews&gt;100" or "p95:load_time&gt;=2000".
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
  /// <exception cref="FormatException">The text is not a condition.</exception>
  public static MetricFilter Parse(string expression) => Parsing.Parse(expression, "metric condition", ParseCore);

  /// <summary>
  /// Tries to parse a condition. See <see cref="Parse"/>.
  /// </summary>
  public static bool TryParse(string expression, out MetricFilter filter) => Parsing.TryParse(expression, Parse, out filter);

  static MetricFilter ParseCore(string expression)
  {
    // The operator is the last one followed by a number, so metric names may contain operator characters.
    for (var at = expression.Length - 1; at > 0; --at)
    {
      foreach (var (token, op) in Tokens)
      {
        if (string.CompareOrdinal(expression, at, token, 0, token.Length) != 0)
          continue;
        var number = expression[(at + token.Length)..].Trim();
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
          continue;
        var metric = expression[..at].Trim();
        // "a>=5" also contains ">" followed by "=5"; the longer token at the earlier position wins.
        if (token.Length == 1 && at > 0 && expression[at - 1] is '>' or '<' or '=' or '!')
          continue;
        return new MetricFilter(Metric.Parse(metric), op, value);
      }
    }
    throw new FormatException($"'{expression}' is not a metric condition, for example 'pageviews>100'.");
  }

  /// <inheritdoc/>
  public override string ToString() =>
      Metric.Key + Tokens.First(x => x.Operator == Operator).Token + Value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// The text tokens of the comparison operators, shared by the filter syntaxes. Longer tokens come
/// first, so "&gt;=" is not read as "&gt;".
/// </summary>
static class ComparisonTokens
{
  public static readonly (string Token, ComparisonOperator Operator)[] All =
  [
    ("==", ComparisonOperator.Equal),
    ("!=", ComparisonOperator.NotEqual),
    (">=", ComparisonOperator.GreaterThanOrEqual),
    ("<=", ComparisonOperator.LessThanOrEqual),
    (">", ComparisonOperator.GreaterThan),
    ("<", ComparisonOperator.LessThan),
  ];
}
