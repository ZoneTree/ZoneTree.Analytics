var throughput = await analytics.QueryAsync(new AnalyticsQuery
{
    Site = "service",
    Event = "request_completed",
    Range = DateRange.Last(TimeSpan.FromHours(1)),
    Interval = TimeInterval.Minutes(5),
    Filters = { Filter.Equal("route", "/orders/:id") },
    Metrics =
    {
        Metric.Rate(Metric.Events, TimeSpan.FromMinutes(1)),
        Metric.Average("duration_ms"),
    },
    Fill = FillMode.None,
});
