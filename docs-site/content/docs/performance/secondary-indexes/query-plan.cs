Console.WriteLine($"Complete coverage: {result.Plan.IsComplete}");
Console.WriteLine($"Rollup index used: {result.Plan.UsedRollupIndexes}");
Console.WriteLine($"Dimension index used: {result.Plan.UsedDimensionIndexes}");

foreach (var step in result.Plan.Steps)
    Console.WriteLine($"{step.Event}: {step.Source}, " +
        $"{step.From:O} to {step.To:O}, index: {step.IndexDimension ?? "none"}");

foreach (var warning in result.Plan.Warnings)
    Console.WriteLine($"Warning: {warning}");

foreach (var notice in result.Plan.Notices)
    Console.WriteLine($"Notice: {notice}");
