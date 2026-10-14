using ZoneTree.Analytics;

// All sample engines use the same explicit combinations so overview filters have equal coverage.
static class SampleAnalyticsOptions
{
  public static void ConfigureIndexes(AnalyticsOptions options)
  {
    options.Storage.DimensionIndexes.Dimensions.AddRange(["source", "country", "device", "page"]);
    options.Storage.RollupIndexes.Definitions.AddRange([
        new("source", "country"), new("source", "device"), new("source", "page"),
        new("country", "source"), new("country", "device"), new("country", "page"),
        new("device", "source"), new("device", "country"), new("device", "page"),
        new("page", "source"), new("page", "country"), new("page", "device")
    ]);
  }
}
