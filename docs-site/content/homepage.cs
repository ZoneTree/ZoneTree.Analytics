using ZoneTree.Analytics;
using ZoneTree.Analytics.Web;

await using var analytics = AnalyticsEngine.Open("data/analytics");

analytics.Track(AnalyticsEvent.PageView("https://example.com/pricing"));
analytics.Track(new AnalyticsEvent("purchase").WithValue("revenue", 49.0));

var traffic = await analytics.Web.GetSummaryAsync(new WebReportRequest());
var sales = await analytics.QueryAsync(new AnalyticsQuery
{
    Event = "purchase",
    Metrics = { Metric.Events, Metric.Sum("revenue") },
});
