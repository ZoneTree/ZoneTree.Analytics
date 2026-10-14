options.Storage.RollupIndexes.Definitions.Add(
    new RollupIndexDefinition(
        StandardDimensions.Source, StandardDimensions.Country));
options.Storage.RollupIndexes.Definitions.Add(
    new RollupIndexDefinition(
        StandardDimensions.Country, StandardDimensions.Source));

