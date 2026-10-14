options.Rollups.ValueAggregates["load_time"] = ValueAggregates.Percentiles;
options.Rollups.ValueAggregates["temperature"] = ValueAggregates.FirstAndLast;
options.Rollups.DistinctDimensions.Add("tenant");

