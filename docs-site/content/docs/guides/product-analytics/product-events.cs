const string customerId = "customer-42";

await analytics.TrackAsync(new AnalyticsEvent("checkout_started")
{
    Site = "shop",
    VisitorId = customerId,
    Dimensions = { ["plan"] = "pro" },
});

await analytics.TrackAsync(new AnalyticsEvent("purchase")
{
    Site = "shop",
    VisitorId = customerId,
    Dimensions = { ["plan"] = "pro" },
    Values = { ["revenue_usd"] = 49.0 },
});
