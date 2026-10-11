using System.Globalization;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>Evaluates metrics and applies filling, ordering, paging and comparison to aggregated groups.</summary>
static class QueryResultBuilder
{
  public static (AnalyticsResult, Dictionary<AnalyticsRow, int>) Build(
      QueryContext context, AnalyticsQuery query, bool includeBucketIndexes)
  {
    var shape = new AnalyticsResultShape(context.Metrics, context.GroupBy);
    var metrics = context.Metrics;

    // Totals per source: every group merged, unique visitors de-duplicated.
    var totals = new Dictionary<string, RollupAccumulator>(StringComparer.Ordinal);
    var keys = new HashSet<GroupKey>();
    // Rows of the base event come from its unconditioned sub-query, whose key is the event name.
    var rowsFromBaseOnly = query.OnlyRowsWithBaseEvent;
    foreach (var source in context.Sources.Values)
    {
      var total = new RollupAccumulator();
      var addsRows = !rowsFromBaseOnly || source.Key == context.BaseEvent;
      foreach (var (key, accumulator) in source.Groups)
      {
        if (!accumulator.HasData)
          continue;
        total.Merge(accumulator);
        if (addsRows && (!rowsFromBaseOnly || accumulator.Count > 0))
          keys.Add(key);
      }
      totals[source.Key] = total;
    }

    var interval = context.Interval;
    var rowKeys = keys.ToList();
    var dataRows = rowKeys.Count;
    if (interval != TimeInterval.None && context.Fill != FillMode.None)
      AddFilledRows(context, keys, rowKeys);

    // Ordered groups are needed by series functions and interpolation, including Having-only metrics.
    var needsSeries = metrics.Any(x => x.NeedsSeries) || context.Having.Any(x => x.Metric.NeedsSeries) ||
        (rowKeys.Count > dataRows && context.Fill is FillMode.Previous or FillMode.Linear);
    var series = needsSeries ? Enumerable.Range(0, rowKeys.Count)
        .GroupBy(i => rowKeys[i].Values, StringComparer.Ordinal)
        .Select(group => group.OrderBy(i => rowKeys[i].Bucket).ToArray())
        .ToArray() : [];
    var effectiveTo = Math.Min(context.To, Math.Max(context.From, context.NowTicks));
    var evaluator = new MetricEvaluator(
        rowKeys,
        dataRows,
        context.Sources,
        totals,
        context.BaseEvent,
        series,
        context.Fill,
        () => RowSeconds(context, rowKeys, effectiveTo),
        (effectiveTo - context.From) / (double)TimeSpan.TicksPerSecond);
    var columns = metrics.Select(evaluator.Evaluate).ToArray();
    var having = context.Having.Select(x => (Filter: x, Values: evaluator.Evaluate(x.Metric).Rows)).ToArray();

    var rows = new List<(AnalyticsRow Row, long Bucket)>(rowKeys.Count);
    for (var r = 0; r < rowKeys.Count; ++r)
    {
      if (!Array.TrueForAll(having, x => x.Filter.IsMatch(x.Values[r])))
        continue;
      var key = rowKeys[r];
      var values = new double[metrics.Length];
      for (var m = 0; m < metrics.Length; ++m)
        values[m] = columns[m].Rows[r];
      var dimensions = KeyCodec.SplitValues(key.Values, context.GroupBy.Length);
      for (var i = 0; i < dimensions.Length; ++i)
        dimensions[i] = KeyCodec.Public(dimensions[i]);
      var label = interval == TimeInterval.None ? (DateTimeOffset?)null : context.Calendar.BucketLabel(key.Bucket, interval);
      rows.Add((new AnalyticsRow(shape, label, dimensions, values), key.Bucket));
    }

    var orders = query.OrderBy.Where(x => x != null).ToList();
    if (orders.Count == 0)
      orders.Add(interval != TimeInterval.None ? Order.ByBucket() : Order.By(metrics[0]));
    var sortColumns = orders.Select(order => new SortColumn(
        order.IsBucket, order.Metric != null, order.Descending,
        order.IsBucket ? -1 : order.Metric != null ? shape.IndexOf(order.Metric) : shape.IndexOfDimension(order.Dimension)))
        .ToArray();
    rows.Sort((a, b) => CompareRows(a, b, sortColumns));
    if (query.LimitPerBucket is { } perBucket)
    {
      var kept = new Dictionary<long, int>();
      rows.RemoveAll(row =>
      {
        var count = kept.GetValueOrDefault(row.Bucket);
        kept[row.Bucket] = count + 1;
        return count >= perBucket;
      });
    }

    Dictionary<AnalyticsRow, int> bucketIndexes = null;
    if (includeBucketIndexes && interval != TimeInterval.None)
    {
      bucketIndexes = new(ReferenceEqualityComparer.Instance);
      var positions = context.Calendar.EnumerateBuckets(context.From, context.To, interval)
          .Select((bucket, index) => (bucket, index))
          .ToDictionary(x => x.bucket, x => x.index);
      foreach (var (row, bucket) in rows)
        bucketIndexes[row] = positions.TryGetValue(bucket, out var index) ? index : -1;
    }

    var totalRows = rows.Count;
    IEnumerable<AnalyticsRow> page = rows.Select(x => x.Row).Skip(query.Offset);
    if (query.Limit.HasValue)
      page = page.Take(query.Limit.Value);

    var totalsRow = new AnalyticsRow(
        shape, null, new string[context.GroupBy.Length], columns.Select(x => x.Total).ToArray());
    var timeZone = context.Calendar.TimeZone;
    var result = new AnalyticsResult(
        shape,
        interval,
        TimeZoneInfo.ConvertTime(new DateTimeOffset(context.From, TimeSpan.Zero), timeZone),
        TimeZoneInfo.ConvertTime(new DateTimeOffset(context.To, TimeSpan.Zero), timeZone),
        timeZone,
        page.ToList(),
        totalRows,
        totalsRow,
        new QueryPlan(context.Steps, [.. context.Warnings]));
    return (result, bucketIndexes);
  }

  /// <summary>
  /// Adds a row for every bucket of the range that a group has no data for. Without group-by
  /// dimensions there is one group, so an empty range still has a row per bucket.
  /// </summary>
  static void AddFilledRows(QueryContext context, HashSet<GroupKey> keys, List<GroupKey> rowKeys)
  {
    var groups = context.GroupBy.Length == 0
        ? [""]
        : rowKeys.Select(x => x.Values).Distinct(StringComparer.Ordinal).ToList();
    var buckets = context.Calendar.EnumerateBuckets(context.From, context.To, context.Interval).ToList();
    if ((long)groups.Count * buckets.Count > context.MaxFilledRows)
      throw new AnalyticsQueryException(
          $"Filling the time series would create more than {context.MaxFilledRows.ToString("N0", CultureInfo.InvariantCulture)} rows " +
          "(Queries.MaxFilledRows). Use a coarser interval, fewer groups or FillMode.None.");
    foreach (var group in groups)
    {
      foreach (var bucket in buckets)
      {
        var key = new GroupKey(bucket, group);
        if (keys.Add(key))
          rowKeys.Add(key);
      }
    }
  }

  /// <summary>
  /// The length of each row's bucket within the range, up to now, in seconds.
  /// </summary>
  static double[] RowSeconds(QueryContext context, List<GroupKey> rowKeys, long effectiveTo)
  {
    var seconds = new double[rowKeys.Count];
    var interval = context.Interval;
    for (var i = 0; i < seconds.Length; ++i)
    {
      long start = context.From, end = effectiveTo;
      if (interval != TimeInterval.None)
      {
        var bucket = rowKeys[i].Bucket;
        start = Math.Max(start, context.Calendar.UtcStartOfBucket(bucket, interval));
        end = Math.Min(end, context.Calendar.UtcStartOfBucket(ReportingCalendar.NextBucket(bucket, interval), interval));
      }
      seconds[i] = Math.Max(0, end - start) / (double)TimeSpan.TicksPerSecond;
    }
    return seconds;
  }

  readonly record struct SortColumn(bool IsBucket, bool IsMetric, bool Descending, int Index);

  static int CompareRows(
      (AnalyticsRow Row, long Bucket) a,
      (AnalyticsRow Row, long Bucket) b,
      SortColumn[] orders)
  {
    foreach (var order in orders)
    {
      int result;
      if (order.IsBucket)
        result = a.Bucket.CompareTo(b.Bucket);
      else if (order.IsMetric)
        result = a.Row.Values[order.Index].CompareTo(b.Row.Values[order.Index]);
      else
        result = string.CompareOrdinal(a.Row.Dimensions[order.Index], b.Row.Dimensions[order.Index]);
      if (order.Descending)
        result = -result;
      if (result != 0)
        return result;
    }
    // Deterministic tie-breakers: bucket, then dimension values.
    var tie = a.Bucket.CompareTo(b.Bucket);
    if (tie != 0)
      return tie;
    for (var i = 0; i < a.Row.Dimensions.Count; ++i)
    {
      tie = string.CompareOrdinal(a.Row.Dimensions[i], b.Row.Dimensions[i]);
      if (tie != 0)
        return tie;
    }
    return 0;
  }

  /// <summary>
  /// Attaches comparison rows by group dimensions and relative bucket position.
  /// </summary>
  public static void AttachComparison(
      AnalyticsResult result,
      Dictionary<AnalyticsRow, int> bucketIndexes,
      AnalyticsResult previous,
      Dictionary<AnalyticsRow, int> previousBucketIndexes)
  {
    static string Key(AnalyticsRow row, Dictionary<AnalyticsRow, int> indexes)
    {
      var index = indexes != null && indexes.TryGetValue(row, out var position) ? position : 0;
      return index.ToString(CultureInfo.InvariantCulture) + KeyCodec.ValueSeparator +
          string.Join(KeyCodec.ValueSeparator, row.Dimensions.Select(KeyCodec.Stored));
    }
    var lookup = new Dictionary<string, AnalyticsRow>(StringComparer.Ordinal);
    foreach (var row in previous.Rows)
      lookup.TryAdd(Key(row, previousBucketIndexes), row);
    foreach (var row in result.Rows)
      row.Previous = lookup.GetValueOrDefault(Key(row, bucketIndexes));
    result.Totals.Previous = previous.Totals;
    result.Comparison = previous;
  }
}
