var options = new AnalyticsOptions
{
    DataDirectory = "data/telemetry",
    DefaultSite = "service",
};
options.Privacy.VisitorIdentification = VisitorIdentification.None;
options.Sessions.Enabled = false;
options.Rollups.Definitions.Clear();
options.Rollups.Definitions.Add(new RollupDefinition()
{
    Events = ["request_completed"],
});
options.Rollups.Definitions.Add(new RollupDefinition("route")
{
    Events = ["request_completed"],
});
options.Rollups.Definitions.Add(new RollupDefinition("route", "status")
{
    Events = ["request_completed"],
});
options.Rollups.ValueAggregates["duration_ms"] = ValueAggregates.Percentiles;

await using var analytics = new AnalyticsEngine(options);
