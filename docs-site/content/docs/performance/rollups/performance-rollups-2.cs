// Apply to the options used to open the next engine.
foreach (var group in result.Plan.RollupSuggestions.GroupBy(x => x.Definition.Id))
{
    var index = options.Rollups.Definitions.FindIndex(x => x.Id == group.Key);
    var current = index >= 0 ? options.Rollups.Definitions[index] : null;
    if (current != null && current.Events == null)
        continue; // This dimension set already covers every event.

    var definition = new RollupDefinition(group.First().Definition.Dimensions.ToArray())
    {
        Events = group.Select(x => x.Event)
            .Concat(current?.Events ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray(),
    };
    if (index < 0)
        options.Rollups.Definitions.Add(definition);
    else
        options.Rollups.Definitions[index] = definition;
}

