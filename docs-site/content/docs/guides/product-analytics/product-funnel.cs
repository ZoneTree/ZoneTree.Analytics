var checkout = await analytics.QueryAsync(new FunnelQuery
{
    Site = "shop",
    Range = DateRange.LastDays(30),
    Window = TimeSpan.FromDays(1),
    Steps =
    {
        FunnelStep.Goal("checkout_started"),
        FunnelStep.Goal("purchase"),
    },
});

Console.WriteLine($"Checkout completion: {checkout.ConversionRate:P1}");
foreach (var step in checkout.Steps)
    Console.WriteLine($"{step.Name}: {step.Visitors} customers");
