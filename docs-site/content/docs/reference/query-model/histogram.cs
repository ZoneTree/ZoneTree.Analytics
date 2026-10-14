var query = new AnalyticsQuery
{
    Range = DateRange.LastDays(30),
    GroupBy = { StandardDimensions.Page },
};
query.Metrics.AddRange(Metric.Histogram(
    "load_time", [0, 100, 250, 500, 1000, double.PositiveInfinity]));
var report = await analytics.QueryAsync(query);
