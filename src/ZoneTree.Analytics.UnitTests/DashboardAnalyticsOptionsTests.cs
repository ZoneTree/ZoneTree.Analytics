using Microsoft.Extensions.Logging.Abstractions;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class DashboardAnalyticsOptionsTests : DirectoryTest
{
  [Test]
  public void ProfilesShareRetentionAndIndexCoverageWithoutSharingMutableOptions()
  {
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    var primary = new AnalyticsOptions();
    DashboardAnalyticsOptions.ConfigurePrimary(primary, "primary", zone);
    var samples = DashboardAnalyticsOptions.CreatePerformanceSamples("samples", zone, 4, NullLogger.Instance);
    var benchmark = DashboardAnalyticsOptions.CreateBenchmark("benchmark", zone, 20, false, NullLogger.Instance);
    var reports = DashboardAnalyticsOptions.CreateBenchmarkReport("retained", zone.Id, true, NullLogger.Instance);
    foreach (var options in new[] { primary, samples, benchmark, reports })
    {
      Assert.That(options.Retention.RawEvents, Is.EqualTo(TimeSpan.FromDays(365 * 3)));
      Assert.That(options.TimeZone.Id, Is.EqualTo(zone.Id));
      Assert.That(options.Web.Bots, Is.EqualTo(BotTraffic.TrackSeparately));
      Assert.That(options.Web.GeoLocator, Is.Null, "real traffic must never get invented countries");
      Assert.That(options.Storage.DimensionIndexes.Dimensions, Is.EquivalentTo(new[] { "source", "country", "device", "page" }));
      Assert.That(options.Storage.RollupIndexes.Definitions.Select(index => string.Join(',', index.Dimensions)),
          Is.EquivalentTo(primary.Storage.RollupIndexes.Definitions.Select(index => string.Join(',', index.Dimensions))));
      Assert.That(options.Storage.RollupIndexes.Definitions, Has.Count.EqualTo(12));
      options.Validate();
    }
    Assert.Multiple(() =>
    {
      Assert.That(primary.DataDirectory, Is.EqualTo("primary"));
      Assert.That(samples.DataDirectory, Is.EqualTo("samples"));
      Assert.That(benchmark.DataDirectory, Is.EqualTo("benchmark"));
      Assert.That(reports.DataDirectory, Is.EqualTo("retained"));
      Assert.That(samples.DefaultSite, Is.EqualTo(DashboardAnalyticsOptions.PerformanceSite));
      Assert.That(benchmark.DefaultSite, Is.EqualTo(DashboardAnalyticsOptions.BenchmarkSite));
      Assert.That(samples.Ingestion.WorkerCount, Is.EqualTo(4));
      Assert.That(benchmark.Ingestion.WorkerCount, Is.EqualTo(20));
      Assert.That(reports.Ingestion.WorkerCount, Is.EqualTo(1));
      // Generated ingestion runs one rollup writer per worker.
      Assert.That(samples.Ingestion.RollupWriterCount, Is.EqualTo(4));
      Assert.That(benchmark.Ingestion.RollupWriterCount, Is.EqualTo(20));
      Assert.That(reports.Ingestion.RollupWriterCount, Is.EqualTo(1));
      Assert.That(benchmark.Ingestion.QueueFullBehavior, Is.EqualTo(QueueFullBehavior.Wait));
      Assert.That(primary.Rollups.ValueAggregates["load_time"], Is.EqualTo(ValueAggregates.Percentiles));
      Assert.That(samples.Rollups.ValueAggregates["load_time"], Is.EqualTo(ValueAggregates.Percentiles));
      Assert.That(benchmark.Rollups.ValueAggregates.ContainsKey("load_time"), Is.False);
      Assert.That(reports.Rollups.ValueAggregates["load_time"], Is.EqualTo(ValueAggregates.Percentiles));
      Assert.That(samples.Storage.Logger, Is.Not.Null);
      Assert.That(benchmark.Storage.Logger, Is.Not.Null);
      Assert.That(reports.Storage.Logger, Is.Not.Null);
    });
    samples.Retention.RawEvents = TimeSpan.FromDays(1);
    samples.Storage.RollupIndexes.Definitions.Clear();
    samples.Storage.DimensionIndexes.Dimensions.Clear();
    samples.Rollups.ValueAggregates.Clear();
    foreach (var options in new[] { primary, benchmark, reports })
    {
      Assert.That(options.Retention.RawEvents, Is.EqualTo(TimeSpan.FromDays(365 * 3)));
      Assert.That(options.Storage.RollupIndexes.Definitions, Has.Count.EqualTo(12));
      Assert.That(options.Storage.DimensionIndexes.Dimensions, Has.Count.EqualTo(4));
    }
    Assert.That(primary.Rollups.ValueAggregates.ContainsKey("load_time"), Is.True);
    Assert.That(reports.Rollups.ValueAggregates.ContainsKey("load_time"), Is.True);
  }

  [TestCase("primary")]
  [TestCase("samples")]
  [TestCase("benchmark")]
  public void EveryEngineRetainsGeneratedHistoryOlderThanNinetyDays(string profile)
  {
    var options = profile switch
    {
      "samples" => DashboardAnalyticsOptions.CreatePerformanceSamples(directory, TimeZoneInfo.Utc, 1, NullLogger.Instance),
      "benchmark" => DashboardAnalyticsOptions.CreateBenchmark(directory, TimeZoneInfo.Utc, 1, false, NullLogger.Instance),
      _ => new AnalyticsOptions()
    };
    if (profile == "primary") DashboardAnalyticsOptions.ConfigurePrimary(options, directory, TimeZoneInfo.Utc);
    options.Retention.CheckInterval = null;
    options.Ingestion.FlushInterval = TimeSpan.FromHours(1);
    options.Storage.Logger ??= new RecordingLogger();
    var at = DateTime.UtcNow.Date.AddDays(-180).AddHours(12);
    using (var engine = new AnalyticsEngine(options))
    {
      Assert.That(engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = "historical", Timestamp = Utc(at), Url = "/blog",
        Dimensions = { ["source"] = "GitHub", ["country"] = "US" }
      }), Is.True);
      engine.Flush();
      var raw = engine.Query(new AnalyticsQuery { Range = Day(at.Date), Source = QuerySource.RawEvents, Metrics = { Metric.Events } });
      Assert.That(raw.Totals[Metric.Events], Is.EqualTo(1));
    }
    using var reopened = new AnalyticsEngine(options);
    Assert.That(reopened.Web.GetSummary(new() { Range = Day(at.Date) }).Summary.PageViews, Is.EqualTo(1));
  }
}
