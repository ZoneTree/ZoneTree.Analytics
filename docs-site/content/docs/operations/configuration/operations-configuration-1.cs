using ZoneTree.Analytics;

var options = new AnalyticsOptions
{
    DataDirectory = "data/analytics",
    DefaultSite = "shop",
    TimeZone = TimeZoneInfo.Utc,
    Retention = { RawEvents = TimeSpan.FromDays(90) },
};

await using var analytics = new AnalyticsEngine(options);

