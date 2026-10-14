options.Enrichers.Add(EventEnricher.NormalizePage(path =>
    System.Text.RegularExpressions.Regex.Replace(path, @"/\d+(?=/|$)", "/:id")));

options.Enrichers.Add(EventEnricher.Create(context =>
{
    var page = context.GetDimension(StandardDimensions.Page);
    var parts = page?.Split('/', StringSplitOptions.RemoveEmptyEntries);
    context.SetDimension("section", parts is { Length: > 0 } ? parts[0] : null);
}));
