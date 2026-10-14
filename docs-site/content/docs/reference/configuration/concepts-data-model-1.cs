using ZoneTree.Analytics;
using ZoneTree.Analytics.Web;

var options = new AnalyticsOptions();
options.Sessions.Dimensions.Add("ab_variant");
options.Rollups.Definitions.Add(new RollupDefinition("ab_variant"));

await using var analytics = new AnalyticsEngine(options);
analytics.Track(AnalyticsEvent.PageView(
    "https://example.com/pricing",
    ipAddress: "203.0.113.7",
    userAgent: "Mozilla/5.0").WithDimension("ab_variant", "B"));

var variants = await analytics.Web.GetBreakdownAsync(
    new WebReportRequest(), "ab_variant");

