await analytics.TrackAsync(new AnalyticsEvent("request_completed")
{
    Site = "service",
    Dimensions =
    {
        ["route"] = "/orders/:id",
        ["status"] = "503",
    },
    Values = { ["duration_ms"] = 182.5 },
});
