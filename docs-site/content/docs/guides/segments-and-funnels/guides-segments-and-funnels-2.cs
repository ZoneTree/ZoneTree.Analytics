var funnel = await analytics.QueryAsync(new FunnelQuery
{
    Range = DateRange.LastDays(30),
    Steps =
    {
        FunnelStep.Page("Pricing", "/pricing"),
        FunnelStep.Page("Signup form", "/signup"),
        FunnelStep.Goal("signup"),
    },
    Window = TimeSpan.FromDays(1),   // null: all steps within one session
});

foreach (var step in funnel.Steps)
    Console.WriteLine($"{step.Name}: {step.Visitors} ({step.ConversionRate:P0}, drop-off {step.DropOff})");

