var purchase = new AnalyticsEvent("purchase")
{
    Site = "shop",
    VisitorId = "customer-42",
    Url = "https://example.com/checkout",
    Dimensions = { ["plan"] = "pro" },
    Values = { ["revenue"] = 49.0 },
};
analytics.Track(purchase);

