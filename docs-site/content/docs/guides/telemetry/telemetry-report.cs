var errors = Metric.Events.Where(Filter.GreaterThanOrEqual("status", 500));
var errorRate = errors / Metric.Events;
var average = Metric.Average("duration_ms");
var p95 = Metric.Percentile("duration_ms", 95);

var health = await analytics.QueryAsync(new AnalyticsQuery
{
    Site = "service",
    Event = "request_completed",
    Range = DateRange.LastDays(7),
    GroupBy = { "route" },
    Metrics = { Metric.Events, errors, errorRate, average, p95 },
    OrderBy = { Order.By(p95) },
});

foreach (var row in health.Rows)
    Console.WriteLine($"{row["route"]}: {row[Metric.Events]} requests, " +
        $"{row[errorRate]:P1} errors, avg {row[average]:F1} ms, p95 {row[p95]:F1} ms");
