using ZoneTree.Analytics;
using ZoneTree.Analytics.Web;

await using var analytics = AnalyticsEngine.Open("data/analytics");
var pageView = AnalyticsEvent.PageView(
    url: "https://example.com/pricing",
    referrer: "https://www.google.com/",
    ipAddress: "203.0.113.7",
    userAgent: "Mozilla/5.0");
pageView.VisitorId = "customer-42";
await analytics.TrackAsync(pageView);

await analytics.TrackAsync(new AnalyticsEvent("purchase")
{
    VisitorId = "customer-42",
    Dimensions = { ["plan"] = "pro" },
    Values = { ["revenue"] = 49.0 },
});

Console.WriteLine("Two events accepted. Press Enter to read reports, or q to close.");
while (Console.ReadLine() is { } input && !input.Equals("q", StringComparison.OrdinalIgnoreCase))
{
    var summary = await analytics.Web.GetSummaryAsync(new WebReportRequest
    {
        Range = DateRange.LastDays(30),
    });
    Console.WriteLine($"{summary.Summary.PageViews} page views");

    var purchases = await analytics.QueryAsync(new AnalyticsQuery
    {
        Event = "purchase",
        Metrics = { Metric.Events, Metric.Sum("revenue") },
    });
    Console.WriteLine($"Revenue: {purchases.Totals[Metric.Sum("revenue")]:F2}");
    Console.WriteLine("Press Enter to refresh, or q to close.");
}

