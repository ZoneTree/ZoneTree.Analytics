options.Storage.DimensionIndexes.Dimensions.AddRange(
    [StandardDimensions.Source, StandardDimensions.Country]);
options.Storage.DimensionIndexes.RecordsPerPartition = 10_000_000;

