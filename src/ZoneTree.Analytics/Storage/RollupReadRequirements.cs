namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The expensive aggregates a query reads. Counts and scalar values are retained because they
/// also determine whether a group has data, including cancellation of sum-only deltas.
/// </summary>
readonly record struct RollupReadRequirements(
    bool Visitors, Dictionary<string, ValueAggregates> ValueDetails, string[] DistinctDimensions);
