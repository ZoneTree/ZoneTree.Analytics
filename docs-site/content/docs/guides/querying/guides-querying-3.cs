var averageOrder = Metric.Sum("revenue") / Metric.Events;

var result = await analytics.QueryAsync(new AnalyticsQuery
{
    Event = "purchase",
    Range = DateRange.ThisMonth,
    Interval = TimeInterval.Day,                              // or TimeInterval.Minutes(5), TimeInterval.Hours(6)
    Metrics =
    {
        averageOrder,
        Metric.Cumulative(Metric.Sum("revenue")),             // month to date
        Metric.MovingAverage(Metric.Visitors, 7),             // 7-day moving average
        Metric.Rate(Metric.Events, TimeSpan.FromHours(1)),    // purchases per hour
    },
});

