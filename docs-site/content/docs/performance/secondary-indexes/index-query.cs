var result = await analytics.QueryAsync(new AnalyticsQuery
{
    Range = DateRange.LastDays(30),
    GroupBy = { StandardDimensions.Country },
    Filters = { Filter.Equal(StandardDimensions.Source, "GitHub") },
    Metrics = { Metric.Visitors, Metric.PageViews },
});
