options.Rollups.Definitions.Add(new RollupDefinition(
    StandardDimensions.Source, StandardDimensions.Country));
options.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] });

