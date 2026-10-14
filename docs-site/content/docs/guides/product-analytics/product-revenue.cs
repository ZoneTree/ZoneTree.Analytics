var revenue = Metric.Sum("revenue_usd");
var averageOrder = revenue / Metric.Events;
var sales = await analytics.QueryAsync(new AnalyticsQuery
{
    Site = "shop",
    Event = "purchase",
    Range = DateRange.LastDays(30),
    GroupBy = { "plan" },
    Metrics = { Metric.Events, Metric.Visitors, revenue, averageOrder },
    OrderBy = { Order.By(revenue) },
});

foreach (var row in sales.Rows)
    Console.WriteLine($"{row["plan"]}: {row[Metric.Events]} purchases, " +
        $"{row[Metric.Visitors]} buyers, USD {row[revenue]:F2} revenue, " +
        $"USD {row[averageOrder]:F2} average order");

Console.WriteLine($"Total buyers: {sales.Totals[Metric.Visitors]}");
