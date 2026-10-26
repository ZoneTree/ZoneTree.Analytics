using Microsoft.Extensions.Logging.Abstractions;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class DashboardAnalyticsOptionsTests : DirectoryTest
{
  [Test]
  public void ProfilesShareRetentionAndIndexCoverageWithoutSharingMutableOptions()
  {
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    var primary = Primary("primary", zone.Id);
    var benchmark = DashboardAnalyticsOptions.CreateBenchmark("benchmark", zone, 20, false, true, NullLogger.Instance);
    var reports = DashboardAnalyticsOptions.CreateBenchmarkReport("retained", zone.Id, true, true, NullLogger.Instance);
    foreach (var options in new[] { primary, benchmark, reports })
    {
      // The primary engine keeps what its settings say: a year of raw events by default.
      Assert.That(options.Retention.RawEvents, Is.EqualTo(TimeSpan.FromDays(options == primary ? 365 : 365 * 3)));
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
      Assert.That(primary.DataDirectory, Is.EqualTo(Path.GetFullPath(Path.Combine(directory, "primary"))));
      Assert.That(benchmark.DataDirectory, Is.EqualTo("benchmark"));
      Assert.That(reports.DataDirectory, Is.EqualTo("retained"));
      Assert.That(benchmark.DefaultSite, Is.EqualTo(DashboardAnalyticsOptions.BenchmarkSite));
      Assert.That(benchmark.Ingestion.WorkerCount, Is.EqualTo(20));
      Assert.That(reports.Ingestion.WorkerCount, Is.EqualTo(1));
      // Generated ingestion runs one rollup writer per worker.
      Assert.That(benchmark.Ingestion.RollupWriterCount, Is.EqualTo(20));
      Assert.That(reports.Ingestion.RollupWriterCount, Is.EqualTo(1));
      Assert.That(benchmark.Ingestion.QueueFullBehavior, Is.EqualTo(QueueFullBehavior.Wait));
      Assert.That(primary.Rollups.ValueAggregates["load_time"], Is.EqualTo(ValueAggregates.Percentiles));
      Assert.That(benchmark.Rollups.ValueAggregates.ContainsKey("load_time"), Is.False);
      Assert.That(reports.Rollups.ValueAggregates["load_time"], Is.EqualTo(ValueAggregates.Percentiles));
      Assert.That(benchmark.Storage.Logger, Is.Not.Null);
      Assert.That(reports.Storage.Logger, Is.Not.Null);
    });
    benchmark.Retention.RawEvents = TimeSpan.FromDays(1);
    benchmark.Storage.RollupIndexes.Definitions.Clear();
    benchmark.Storage.DimensionIndexes.Dimensions.Clear();
    benchmark.Rollups.ValueAggregates["load_time"] = ValueAggregates.None;
    foreach (var options in new[] { primary, reports })
    {
      Assert.That(options.Retention.RawEvents, Is.EqualTo(TimeSpan.FromDays(options == primary ? 365 : 365 * 3)));
      Assert.That(options.Storage.RollupIndexes.Definitions, Has.Count.EqualTo(12));
      Assert.That(options.Storage.DimensionIndexes.Dimensions, Has.Count.EqualTo(4));
    }
    Assert.That(primary.Rollups.ValueAggregates.ContainsKey("load_time"), Is.True);
    Assert.That(reports.Rollups.ValueAggregates.ContainsKey("load_time"), Is.True);
  }

  [TestCase("primary")]
  [TestCase("benchmark")]
  public void EveryEngineRetainsGeneratedHistoryOlderThanNinetyDays(string profile)
  {
    var options = profile switch
    {
      "benchmark" => DashboardAnalyticsOptions.CreateBenchmark(directory, TimeZoneInfo.Utc, 1, false, true, NullLogger.Instance),
      _ => Primary(directory, "UTC")
    };
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

  // The primary engine as the setup wizard's default settings configure it.
  AnalyticsOptions Primary(string dataDirectory, string timeZone)
  {
    var settings = SettingsService.CreateDefaults();
    settings.General.TimeZone = timeZone;
    // Tests download nothing.
    settings.Location.Database = LocationSettings.Off;
    settings.General.DataDirectory = dataDirectory;
    return SettingsMapper.ToOptions(settings, directory, null);
  }
}
