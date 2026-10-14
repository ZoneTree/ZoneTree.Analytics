var buyers = new AnalyticsQuery
{
    Metrics = { Metric.Visitors },
    Segments = { Segment.VisitorsWho("purchase") },
};

var pricingVisits = new AnalyticsQuery
{
    Metrics = { Metric.Visitors },
    Segments =
    {
        Segment.SessionsWith(
            StandardEvents.PageView,
            Filter.Equal(StandardDimensions.Page, "/pricing")),
    },
};

var nonBuyers = new AnalyticsQuery
{
    Metrics = { Metric.Visitors },
    Segments = { Segment.VisitorsWho("purchase").Exclude() },
};

var report = await analytics.QueryAsync(buyers);

