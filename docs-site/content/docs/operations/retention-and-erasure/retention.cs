var options = new AnalyticsOptions
{
    Retention =
    {
        RawEvents = TimeSpan.FromDays(30),
        HourRollups = TimeSpan.FromDays(180),
        DayRollups = null,
    },
};

await using var analytics = new AnalyticsEngine(options);
// Scheduled cleanup remains enabled with its default interval.

