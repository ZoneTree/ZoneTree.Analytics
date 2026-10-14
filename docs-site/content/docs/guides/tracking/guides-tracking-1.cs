options.Web.Bots = BotTraffic.TrackSeparately;

var summary = (await analytics.Web.GetSummaryAsync(request)).Summary;   // BotHits next to PageViews
var pages   = await analytics.Web.GetPagesAsync(request);     // PageStats.BotHits per page
var bots    = await analytics.Web.GetBotsAsync(request);      // name, category, hits and share

// Which pages do AI crawlers read?
var ai = await analytics.QueryAsync(new AnalyticsQuery
{
    Event = StandardEvents.Bot,
    GroupBy = { StandardDimensions.Page },
    Filters = { Filter.Equal(StandardDimensions.BotCategory, BotCategories.AI) },
    Metrics = { Metric.Events },
});

