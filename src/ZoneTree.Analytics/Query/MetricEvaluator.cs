using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>
/// Computes the values of metrics for the rows of a result, one column per metric.
/// </summary>
/// <remarks>
/// Aggregates are read from the aggregated groups of each row, and buckets added by filling are
/// filled per <see cref="FillMode"/>. Formulas combine the columns of their operands. Series
/// functions run along the rows of each group in bucket order. Each distinct metric is computed
/// once, however often it appears in a query.
/// </remarks>
sealed class MetricEvaluator
{
  /// <summary>
  /// The values of a metric: one per row, and the value of the totals row.
  /// </summary>
  public readonly record struct Column(double[] Rows, double Total);

  static readonly Func<string, RollupAccumulator> NoData = _ => null;

  readonly Func<string, RollupAccumulator>[] rows;

  readonly Func<string, RollupAccumulator> totals;

  readonly string baseEvent;

  readonly bool[] filled;

  readonly int[][] series;

  readonly FillMode fill;

  readonly Func<double[]> rowSeconds;

  readonly double rangeSeconds;

  readonly Dictionary<string, Column> columns = new(StringComparer.Ordinal);

  double[] cachedRowSeconds;

  /// <param name="rows">The aggregated groups of each row, by event; null for filled rows.</param>
  /// <param name="totals">The totals, by event.</param>
  /// <param name="baseEvent">The query's base event.</param>
  /// <param name="series">The rows of each group, in bucket order.</param>
  /// <param name="fill">How filled rows get their aggregate values.</param>
  /// <param name="rowSeconds">Computes the length of each row's bucket within the range, for rates.</param>
  /// <param name="rangeSeconds">The length of the range, for the rate of the totals.</param>
  public MetricEvaluator(
      Func<string, RollupAccumulator>[] rows,
      Func<string, RollupAccumulator> totals,
      string baseEvent,
      int[][] series,
      FillMode fill,
      Func<double[]> rowSeconds,
      double rangeSeconds)
  {
    this.rows = rows;
    this.totals = totals;
    this.baseEvent = baseEvent;
    this.series = series;
    this.fill = fill;
    this.rowSeconds = rowSeconds;
    this.rangeSeconds = rangeSeconds;
    filled = rows.Select(x => x == null).ToArray();
  }

  public Column Evaluate(Metric metric)
  {
    if (columns.TryGetValue(metric.Key, out var column))
      return column;
    column = Compute(metric);
    columns.Add(metric.Key, column);
    return column;
  }

  Column Compute(Metric metric)
  {
    if (metric.IsAggregate)
      return Aggregate(metric);
    var operands = metric.Operands.Select(Evaluate).ToArray();
    return metric.MetricKind switch
    {
      Metric.Kind.Constant => new Column(Fill(metric.Parameter), metric.Parameter),
      Metric.Kind.Add => Combine(operands[0], operands[1], static (a, b) => a + b),
      Metric.Kind.Subtract => Combine(operands[0], operands[1], static (a, b) => a - b),
      Metric.Kind.Multiply => Combine(operands[0], operands[1], static (a, b) => a * b),
      Metric.Kind.Divide => Combine(operands[0], operands[1], Divide),
      Metric.Kind.PercentOfTotal => PercentOfTotal(operands[0]),
      Metric.Kind.Rate => Rate(operands[0], metric.Parameter),
      Metric.Kind.Cumulative => Cumulative(operands[0]),
      Metric.Kind.MovingAverage => MovingAverage(operands[0], (int)metric.Parameter),
      Metric.Kind.Difference => Changes(operands[0], counterResets: false),
      Metric.Kind.Increase => Changes(operands[0], counterResets: true),
      Metric.Kind.Lag => Shift(operands[0], -(int)metric.Parameter),
      Metric.Kind.Lead => Shift(operands[0], (int)metric.Parameter),
      _ => throw new InvalidOperationException($"Unknown metric '{metric.Key}'.")
    };
  }

  static double Divide(double numerator, double denominator) => denominator == 0 ? 0 : numerator / denominator;

  double[] Fill(double value)
  {
    var values = new double[rows.Length];
    Array.Fill(values, value);
    return values;
  }

  Column Aggregate(Metric metric)
  {
    var values = new double[rows.Length];
    var hasFilled = false;
    for (var i = 0; i < rows.Length; ++i)
    {
      if (filled[i])
        hasFilled = true;
      else
        values[i] = metric.EvaluateAggregate(rows[i], baseEvent);
    }
    if (hasFilled && fill is FillMode.Previous or FillMode.Linear)
      FillGaps(values);
    return new Column(values, metric.EvaluateAggregate(totals, baseEvent));
  }

  /// <summary>
  /// Gives filled rows the previous values with data, or values interpolated between the rows
  /// with data around them. Leading and trailing filled rows take the nearest values with data.
  /// </summary>
  void FillGaps(double[] values)
  {
    foreach (var rowsOfGroup in series)
    {
      var previous = -1;
      for (var position = 0; position <= rowsOfGroup.Length; ++position)
      {
        if (position < rowsOfGroup.Length && filled[rowsOfGroup[position]])
          continue;
        // Positions previous + 1 .. position - 1 are filled rows between two rows with data.
        var next = position < rowsOfGroup.Length ? position : -1;
        for (var gap = previous + 1; gap < position; ++gap)
        {
          double value;
          if (previous < 0 && next < 0)
            value = 0;
          else if (previous < 0)
            value = values[rowsOfGroup[next]];
          else if (next < 0 || fill == FillMode.Previous)
            value = values[rowsOfGroup[previous]];
          else
          {
            var from = values[rowsOfGroup[previous]];
            var to = values[rowsOfGroup[next]];
            value = from + (to - from) * (gap - previous) / (next - previous);
          }
          values[rowsOfGroup[gap]] = value;
        }
        previous = position;
      }
    }
  }

  static Column Combine(Column left, Column right, Func<double, double, double> operation)
  {
    var values = new double[left.Rows.Length];
    for (var i = 0; i < values.Length; ++i)
      values[i] = Finite(operation(left.Rows[i], right.Rows[i]));
    return new Column(values, Finite(operation(left.Total, right.Total)));
  }

  /// <summary>
  /// Results stay finite: a formula that overflows gives zero, like a division by zero.
  /// </summary>
  static double Finite(double value) => double.IsFinite(value) ? value : 0;

  static Column PercentOfTotal(Column inner)
  {
    var values = new double[inner.Rows.Length];
    if (inner.Total != 0)
    {
      for (var i = 0; i < values.Length; ++i)
        values[i] = inner.Rows[i] / inner.Total;
    }
    return new Column(values, inner.Total != 0 ? 1 : 0);
  }

  Column Rate(Column inner, double unitSeconds)
  {
    var seconds = cachedRowSeconds ??= rowSeconds();
    var values = new double[inner.Rows.Length];
    for (var i = 0; i < values.Length; ++i)
      values[i] = seconds[i] > 0 ? inner.Rows[i] / seconds[i] * unitSeconds : 0;
    return new Column(values, rangeSeconds > 0 ? inner.Total / rangeSeconds * unitSeconds : 0);
  }

  Column Cumulative(Column inner)
  {
    var values = new double[inner.Rows.Length];
    foreach (var rowsOfGroup in series)
    {
      double sum = 0;
      foreach (var row in rowsOfGroup)
      {
        sum += inner.Rows[row];
        values[row] = sum;
      }
    }
    return new Column(values, inner.Total);
  }

  Column MovingAverage(Column inner, int window)
  {
    var values = new double[inner.Rows.Length];
    foreach (var rowsOfGroup in series)
    {
      double sum = 0;
      for (var position = 0; position < rowsOfGroup.Length; ++position)
      {
        sum += inner.Rows[rowsOfGroup[position]];
        if (position >= window)
          sum -= inner.Rows[rowsOfGroup[position - window]];
        values[rowsOfGroup[position]] = sum / Math.Min(position + 1, window);
      }
    }
    return new Column(values, inner.Total);
  }

  /// <summary>
  /// The value <paramref name="offset"/> buckets later (or earlier, when negative) in each
  /// group; zero where there is no such bucket. The total is the metric's total.
  /// </summary>
  Column Shift(Column inner, int offset)
  {
    var values = new double[inner.Rows.Length];
    foreach (var rowsOfGroup in series)
    {
      for (var position = 0; position < rowsOfGroup.Length; ++position)
      {
        var source = position + offset;
        if (source >= 0 && source < rowsOfGroup.Length)
          values[rowsOfGroup[position]] = inner.Rows[rowsOfGroup[source]];
      }
    }
    return new Column(values, inner.Total);
  }

  /// <summary>
  /// The change from the previous bucket of each group; with <paramref name="counterResets"/>, a
  /// decrease counts as a reset and the change is the current value. The total is the sum.
  /// </summary>
  Column Changes(Column inner, bool counterResets)
  {
    var values = new double[inner.Rows.Length];
    double total = 0;
    foreach (var rowsOfGroup in series)
    {
      for (var position = 1; position < rowsOfGroup.Length; ++position)
      {
        var current = inner.Rows[rowsOfGroup[position]];
        var change = current - inner.Rows[rowsOfGroup[position - 1]];
        if (counterResets && change < 0)
          change = current;
        values[rowsOfGroup[position]] = change;
        total += change;
      }
    }
    return new Column(values, total);
  }
}
