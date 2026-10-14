var pageView = AnalyticsEvent.PageView(
    "https://example.com/pricing?utm_source=newsletter",
    "https://www.google.com/",
    "203.0.113.7",
    "Mozilla/5.0");
pageView.VisitorId = "customer-42";
analytics.Track(pageView);

bool accepted = await analytics.TrackAsync(
    new AnalyticsEvent("purchase") { VisitorId = "customer-42" }
        .WithDimension("plan", "pro")
        .WithValue("revenue", 49.0),
    cancellationToken);

