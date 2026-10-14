var result = await analytics.QueryAsync(new AnalyticsQuery
{
    Range = DateRange.LastDays(30),
    GroupBy = { StandardDimensions.Source },
    Metrics = { Metric.Visitors, Metric.BounceRate, Metric.ConversionRate("purchase") },
    OrderBy = { Order.By(Metric.Visitors) },
    Limit = 10,
});

foreach (var row in result.Rows)
    Console.WriteLine($"{row[StandardDimensions.Source]}: {row[Metric.Visitors]} visitors, " +
        $"{row[Metric.BounceRate]:P0} bounce, {row[Metric.ConversionRate("purchase")]:P1} converted");

Console.WriteLine($"{result.Totals[Metric.Visitors]} visitors in total");

