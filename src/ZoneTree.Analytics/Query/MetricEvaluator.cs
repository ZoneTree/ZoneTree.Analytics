using ZoneTree.Analytics.Storage;
using ZoneTree.Analytics.Internal;

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

  readonly IReadOnlyList<GroupKey> rows;

  readonly int dataRows;

  readonly Dictionary<string, SourceContext> sources;

  readonly Dictionary<string, RollupAccumulator> totals;

  readonly string baseEvent;

  readonly int[][] series;

  readonly FillMode fill;

  readonly Func<double[]> rowSeconds;

  readonly double rangeSeconds;

  readonly TimeInterval interval;

  readonly long firstBucketIndex;

  readonly Dictionary<string, Column> columns = new(StringComparer.Ordinal);

  double[] cachedRowSeconds;

  long[] cachedBucketIndexes;

  /// <param name="rows">The group keys of data rows, followed by rows added by filling.</param>
  /// <param name="dataRows">The number of rows with data, before filling.</param>
  /// <param name="sources">The aggregated groups, by source.</param>
  /// <param name="totals">The totals, by event.</param>
  /// <param name="baseEvent">The query's base event.</param>
  /// <param name="series">The rows of each group, in bucket order.</param>
  /// <param name="fill">How filled rows get their aggregate values.</param>
  /// <param name="rowSeconds">Computes the length of each row's bucket within the range, for rates.</param>
  /// <param name="rangeSeconds">The length of the range, for the rate of the totals.</param>
  /// <param name="interval">The interval of the bucket keys.</param>
  /// <param name="firstBucket">The first bucket of the requested range.</param>
  public MetricEvaluator(
      IReadOnlyList<GroupKey> rows,
      int dataRows,
      Dictionary<string, SourceContext> sources,
      Dictionary<string, RollupAccumulator> totals,
      string baseEvent,
      int[][] series,
      FillMode fill,
      Func<double[]> rowSeconds,
      double rangeSeconds,
      TimeInterval interval,
      long firstBucket)
  {
    this.rows = rows;
    this.dataRows = dataRows;
    this.sources = sources;
    this.totals = totals;
    this.baseEvent = baseEvent;
    this.series = series;
    this.fill = fill;
    this.rowSeconds = rowSeconds;
    this.rangeSeconds = rangeSeconds;
    this.interval = interval;
    firstBucketIndex = ReportingCalendar.BucketIndex(firstBucket, interval);
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
    var values = new double[rows.Count];
    Array.Fill(values, value);
    return values;
  }

  Column Aggregate(Metric metric)
  {
    // Conversion rates read the goal source first and the base source second; other aggregates read one source.
    var sourceKeys = metric.Sources(baseEvent).Select(metric.SourceKey).ToArray();
    var groups = sources.GetValueOrDefault(sourceKeys[0])?.Groups;
    var baseGroups = sourceKeys.Length > 1 ? sources.GetValueOrDefault(sourceKeys[1])?.Groups : null;
    var values = new double[rows.Count];
    for (var i = 0; i < dataRows; ++i)
      values[i] = metric.EvaluateAggregate(groups?.GetValueOrDefault(rows[i]), baseGroups?.GetValueOrDefault(rows[i]));
    if (dataRows < rows.Count && fill is FillMode.Previous or FillMode.Linear)
      FillGaps(values);
    return new Column(values, metric.EvaluateAggregate(
        totals.GetValueOrDefault(sourceKeys[0]), sourceKeys.Length > 1 ? totals.GetValueOrDefault(sourceKeys[1]) : null));
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
        if (position < rowsOfGroup.Length && rowsOfGroup[position] >= dataRows)
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
            value = Interpolate(from, to, (double)(gap - previous) / (next - previous));
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

  static double Interpolate(double from, double to, double weight)
  {
    if (from == to)
      return from;
    // Opposite signs can overflow the difference, while their convex sum stays finite.
    return from < 0 && to > 0 || from > 0 && to < 0
        ? from * (1 - weight) + to * weight
        : from + (to - from) * weight;
  }

  long[] BucketIndexes => cachedBucketIndexes ??=
      rows.Select(row => ReportingCalendar.BucketIndex(row.Bucket, interval)).ToArray();

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
    var indexes = BucketIndexes;
    foreach (var rowsOfGroup in series)
    {
      var capacity = 1;
      while (capacity < Math.Min(window, rowsOfGroup.Length))
        capacity *= 2;
      var tree = new WindowSum[capacity * 2];
      var first = Math.Min(firstBucketIndex, indexes[rowsOfGroup[0]]);
      var left = 0;
      for (var position = 0; position < rowsOfGroup.Length; ++position)
      {
        var row = rowsOfGroup[position];
        var start = indexes[row] - window + 1;
        while (left < position && indexes[rowsOfGroup[left]] < start)
          SetWindowValue(tree, capacity, left++, default);
        SetWindowValue(tree, capacity, position, WindowSum.Of(inner.Rows[row]));
        var buckets = (int)Math.Min(window, indexes[row] - first + 1);
        values[row] = tree[1].Average(buckets);
      }
    }
    return new Column(values, inner.Total);
  }

  /// <summary>
  /// A scaled, compensated sum of the current leaves. Updating a leaf recomputes its path,
  /// so removed outliers leave no cancellation error behind. Scaling prevents a finite
  /// window mean from overflowing just because its unscaled sum does.
  /// </summary>
  readonly record struct WindowSum(double Scale, double Sum, double Correction)
  {
    public static WindowSum Of(double value)
    {
      if (!double.IsFinite(value))
        return new(double.PositiveInfinity, value, 0);
      if (value == 0)
        return default;
      // Powers of two make rescaling exact, avoiding new rounding in ordinary sums.
      var scale = Math.ScaleB(1, Math.ILogB(Math.Abs(value)));
      return new(scale, value / scale, 0);
    }

    public double Average(int count) => double.IsFinite(Scale)
        ? Math.Clamp((Sum / count + Correction / count) * Scale, -double.MaxValue, double.MaxValue)
        : Sum;

    public static WindowSum Combine(WindowSum a, WindowSum b)
    {
      if (a.Scale == 0)
        return b;
      if (b.Scale == 0)
        return a;
      if (!double.IsFinite(a.Scale) || !double.IsFinite(b.Scale))
        return new(double.PositiveInfinity,
            (double.IsFinite(a.Scale) ? 0 : a.Sum) + (double.IsFinite(b.Scale) ? 0 : b.Sum), 0);
      var scale = Math.Max(a.Scale, b.Scale);
      var left = a.Scale / scale;
      var right = b.Scale / scale;
      double sum = 0, correction = 0;
      Add(ref sum, ref correction, a.Sum * left);
      Add(ref sum, ref correction, a.Correction * left);
      Add(ref sum, ref correction, b.Sum * right);
      Add(ref sum, ref correction, b.Correction * right);
      return new(scale, sum, correction);
    }

    static void Add(ref double sum, ref double correction, double value)
    {
      var next = sum + value;
      correction += Math.Abs(sum) >= Math.Abs(value) ? (sum - next) + value : (value - next) + sum;
      sum = next;
    }
  }

  static void SetWindowValue(WindowSum[] tree, int capacity, int position, WindowSum value)
  {
    var index = capacity + (position & (capacity - 1));
    tree[index] = value;
    while ((index /= 2) > 0)
      tree[index] = WindowSum.Combine(tree[index * 2], tree[index * 2 + 1]);
  }

  /// <summary>
  /// The value <paramref name="offset"/> buckets later (or earlier, when negative) in each
  /// group; zero where there is no such bucket. The total is the metric's total.
  /// </summary>
  Column Shift(Column inner, int offset)
  {
    var values = new double[inner.Rows.Length];
    var indexes = BucketIndexes;
    foreach (var rowsOfGroup in series)
    {
      var source = 0;
      for (var position = 0; position < rowsOfGroup.Length; ++position)
      {
        var target = indexes[rowsOfGroup[position]] + offset;
        while (source < rowsOfGroup.Length && indexes[rowsOfGroup[source]] < target)
          ++source;
        if (source < rowsOfGroup.Length && indexes[rowsOfGroup[source]] == target)
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
