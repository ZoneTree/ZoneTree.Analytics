using System.Text.RegularExpressions;
using ZoneTree.Analytics.Web;
using ZoneTree.Options;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Keeps the README examples compiling and working.
/// </summary>
public sealed class ReadmeExamplesTests
{
  [Test]
  public async Task QuickStartAndQueries()
  {
    var directory = TestEngine.NewDirectory("readme");
    try
    {
      var options = new AnalyticsOptions
      {
        DataDirectory = directory,
        DefaultSite = "example.com",
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"),
        Ingestion =
        {
          WorkerCount = 2,
          QueueCapacity = 100_000,
          QueueFullBehavior = QueueFullBehavior.Drop,
          FlushInterval = TimeSpan.FromSeconds(2),
        },
        Sessions = { Timeout = TimeSpan.FromMinutes(30) },
        Rollups =
        {
          Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day,
          HyperLogLogPrecision = 14,
        },
        Retention =
        {
          RawEvents = TimeSpan.FromDays(90),
          MinuteRollups = TimeSpan.FromDays(2),
          HourRollups = TimeSpan.FromDays(400),
          DayRollups = null,
        },
        Privacy = { GeoPrecision = GeoPrecision.Country },
        Web =
        {
          GeoLocator = new StubGeoLocator(),
          PathNormalizer = path => Regex.Replace(path, @"/\d+", "/:id"),
          ReferrerSources = new Dictionary<string, string> { ["lobste.rs"] = "Lobsters" },
        },
        Storage =
        {
          StoreRawEvents = true,
          RawEventPartitioning = PartitionPeriod.Month,
          WriteAheadLogMode = WriteAheadLogMode.AsyncCompressed,
        },
      };
      options.Rollups.Definitions.Add(new RollupDefinition(StandardDimensions.Country, StandardDimensions.Page));
      options.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] });

      await using var analytics = new AnalyticsEngine(options);
      analytics.Track(AnalyticsEvent.PageView(
          url: "https://example.com/pricing?utm_source=newsletter",
          referrer: "https://www.google.com/",
          ipAddress: "10.0.0.1",
          userAgent: UserAgents.ChromeWindows));
      analytics.Track(new AnalyticsEvent("purchase")
      {
        VisitorId = "user-1",
        Url = "https://shop.example.com/checkout",
      }
      .WithDimension("plan", "pro")
      .WithMetric("revenue", 49.0));
      analytics.Track(new AnalyticsEvent("job_completed")
          .WithDimension("queue", "emails")
          .WithMetric("duration_ms", 812));
      await analytics.FlushAsync();

      var from = DateTime.UtcNow.AddDays(-30);
      var to = DateTime.UtcNow.AddMinutes(1);
      var summary = analytics.Web.GetSummary(from, to);
      var germanMobile = analytics.Web.GetSummary(from, to, filters:
      [
        DimensionFilter.Equal(StandardDimensions.Country, "DE"),
        DimensionFilter.Equal(StandardDimensions.Device, "Desktop"),
      ]);
      var result = analytics.Query(new AnalyticsQuery()
          .ForEvent("purchase")
          .Between(from, to)
          .Every(TimeInterval.Week)
          .GroupBy("plan")
          .Order(QueryOrder.ByMetricSum("revenue"))
          .Top(10));
      var bySource = analytics.Query(new AnalyticsQuery()
          .ForEvent(StandardEvents.Session)
          .Between(from, to)
          .GroupBy(StandardDimensions.Source));

      Assert.Multiple(() =>
      {
        Assert.That(summary.PageViews, Is.EqualTo(1));
        Assert.That(summary.Visitors, Is.EqualTo(1));
        Assert.That(germanMobile.PageViews, Is.EqualTo(1));
        Assert.That(result.Rows.Single().Dimensions[0], Is.EqualTo("pro"));
        Assert.That(result.Rows.Single().Sum("revenue"), Is.EqualTo(49));
        Assert.That(result.Totals.Visitors, Is.EqualTo(1));
        Assert.That(bySource.Rows.Select(x => x.Dimensions[0]), Does.Contain("Newsletter"));
        Assert.That(analytics.Web.GetCustomEvents(from, to).Select(x => x.Name),
            Is.EquivalentTo(new[] { "purchase", "job_completed" }));
        Assert.That(analytics.Web.GetCurrentVisitors(), Is.EqualTo(2));
      });
    }
    finally
    {
      TestEngine.Delete(directory);
    }
  }
}
