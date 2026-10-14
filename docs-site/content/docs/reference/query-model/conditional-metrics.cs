var errors = Metric.Events.Where(Filter.GreaterThanOrEqual("status", 500));
var report = await analytics.QueryAsync(new AnalyticsQuery
{
    Event = "request_completed",
    GroupBy = { "route" },
    Metrics = { Metric.Events, errors, errors / Metric.Events },
});
