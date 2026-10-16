using Microsoft.Extensions.Logging;
using ZoneTree.Analytics;
using ZoneTree.Analytics.AspNetCore;
using ZoneTree.Analytics.Web;

// Edit engine configuration here. Every engine gets its own options and collections.
static class DashboardAnalyticsOptions
{
  public const string DefaultDataDirectory = "data/analytics";
  public const string PerformanceSite = "performance-samples";
  public const string BenchmarkSite = "benchmark-results";

  // Shared defaults for actual traffic, generated samples and benchmark datasets.
  // Set retention, storage, rollups, query and ingestion tuning here for all engines.
  static void ConfigureCommon(AnalyticsOptions options)
  {
    options.Retention.RawEvents = TimeSpan.FromDays(365 * 3);
    options.Web.Bots = BotTraffic.TrackSeparately;
    // No geolocator: real traffic needs a real one (see WebOptions.GeoLocator). Generated
    // samples carry their country as a dimension.
    ConfigureIndexes(options);
  }

  public static void ConfigurePrimary(AnalyticsOptions options, string directory, TimeZoneInfo timeZone)
  {
    ConfigureCommon(options);
    options.DataDirectory = directory;
    options.TimeZone = timeZone;
    options.Rollups.ValueAggregates["load_time"] = ValueAggregates.Percentiles;
    // ASP.NET Core registration supplies the primary engine's host logger.
  }

  public static AnalyticsOptions CreatePerformanceSamples(string directory, TimeZoneInfo timeZone,
      int workers, ILogger logger = null)
  {
    var options = Create(directory, PerformanceSite, timeZone, logger);
    SetWorkers(options, workers);
    options.Rollups.ValueAggregates["load_time"] = ValueAggregates.Percentiles;
    return options;
  }

  public static AnalyticsOptions CreateBenchmark(string directory, TimeZoneInfo timeZone,
      int workers, bool percentiles, ILogger logger = null)
  {
    var options = Create(directory, BenchmarkSite, timeZone, logger);
    SetWorkers(options, workers);
    options.Ingestion.QueueFullBehavior = QueueFullBehavior.Wait;
    if (percentiles) options.Rollups.ValueAggregates["load_time"] = ValueAggregates.Percentiles;
    return options;
  }

  // Reopening uses the saved dataset time zone and percentile setting, with one worker.
  public static AnalyticsOptions CreateBenchmarkReport(string directory, string timeZoneId,
      bool percentiles, ILogger logger = null) =>
      CreateBenchmark(directory, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), 1, percentiles, logger);

  // Generated ingestion runs as many rollup writers as workers, so the rows that every worker
  // buffers are merged and written in parallel at the same scale.
  static void SetWorkers(AnalyticsOptions options, int workers)
  {
    options.Ingestion.WorkerCount = workers;
    options.Ingestion.RollupWriterCount = workers;
  }

  static AnalyticsOptions Create(string directory, string site, TimeZoneInfo timeZone, ILogger logger)
  {
    var options = new AnalyticsOptions();
    ConfigureCommon(options);
    options.DataDirectory = directory;
    options.DefaultSite = site;
    options.TimeZone = timeZone;
    if (logger != null) options.UseLogger(logger);
    return options;
  }

  // Explicit combinations shared by all profiles so overview filters have equal coverage.
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
