var byHour = new AnalyticsQuery
{
    Range = DateRange.LastDays(30),
    TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"),
    GroupBy = { StandardDimensions.DayOfWeek, StandardDimensions.HourOfDay },
    Filters = { Filter.LessThanOrEqual(StandardDimensions.DayOfWeek, 5) },
    Metrics = { Metric.Events, Metric.Visitors },
};

