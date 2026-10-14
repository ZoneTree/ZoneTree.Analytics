var report = await analytics.QueryAsync(new AnalyticsQuery
{
    Range = DateRange.LastDays(30),
    GroupBy = { StandardDimensions.Source },
    Filters =
    {
        Filter.AnyOf(
            Filter.Equal(StandardDimensions.Country, "DE"),
            Filter.Equal(StandardDimensions.Country, "AT")),
    },
    Metrics = { Metric.Visitors, Metric.PageViews },
});
