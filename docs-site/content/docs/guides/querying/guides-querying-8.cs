foreach (var row in result.Rows)
{
    Console.WriteLine($"{row[StandardDimensions.Source]}: {row[Metric.Visitors]} visitors");
    if (row.Previous is { } previous)
        Console.WriteLine($"Previous: {previous[Metric.Visitors]} visitors");
}
Console.WriteLine($"Total: {result.Totals[Metric.Visitors]} visitors");

