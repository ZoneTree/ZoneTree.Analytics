var result = await analytics.QueryAsync(new AnalyticsQuery
{
    Range = DateRange.LastDays(30),
    GroupBy = { StandardDimensions.Source },
    Filters = { Filter.Equal(StandardDimensions.Country, "US") },
    Metrics = { Metric.Visitors, Metric.PageViews },
});
