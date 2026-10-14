var slowestPages = await analytics.QueryAsync(new AnalyticsQuery
{
    Range = DateRange.LastDays(30),
    Interval = TimeInterval.Day,
    GroupBy = { StandardDimensions.Page },
    Metrics = { Metric.Max("load_time") },
    OrderBy = { Order.ByBucket(), Order.By(Metric.Max("load_time")) },
    LimitPerBucket = 1,
});

