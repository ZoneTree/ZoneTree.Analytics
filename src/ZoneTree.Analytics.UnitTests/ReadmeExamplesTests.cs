using System.Text.RegularExpressions;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Keeps the README examples compiling and working.
/// </summary>
public sealed class ReadmeExamplesTests
{
  [Test]
  public async Task ReadmeExamples()
  {
    var directory = TestEngine.NewDirectory("readme");
    try
    {
      // Configuration
      var options = new AnalyticsOptions
      {
        DataDirectory = directory,
        DefaultSite = "example.com",
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"),
        Ingestion = { WorkerCount = 2, QueueFullBehavior = QueueFullBehavior.Drop },
        Sessions = { Timeout = TimeSpan.FromMinutes(30), Dimensions = { "ab_variant" } },
        Queries = { Timeout = TimeSpan.FromSeconds(10) },
        Rollups = { Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day },
        Retention =
        {
          RawEvents = TimeSpan.FromDays(90),
          MinuteRollups = TimeSpan.FromDays(2),
          HourRollups = TimeSpan.FromDays(400),
          DayRollups = null,
        },
        Storage =
        {
          Partitioning = { RawEvents = PartitionPeriod.Day },
          Rollups = { ConfigureMaintainer = maintainer => maintainer.InactiveBlockCacheCleanupInterval = TimeSpan.FromMinutes(1) },
        },
        Privacy = { GeoPrecision = GeoPrecision.Country },
        Web = { GeoLocator = new StubGeoLocator() },
        Enrichers = { EventEnricher.NormalizePage(path => Regex.Replace(path, @"/\d+", "/:id")) },
      };
      options.Rollups.Definitions.Add(new RollupDefinition(StandardDimensions.Country, StandardDimensions.Page));
      options.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] });

      await using var analytics = new AnalyticsEngine(options);

      // Tracking
      analytics.Track(AnalyticsEvent.PageView(
          url: "https://example.com/pricing?utm_source=newsletter",
          referrer: "https://www.google.com/",
          ipAddress: "10.0.0.1",
          userAgent: UserAgents.ChromeWindows));
      analytics.Track(new AnalyticsEvent("purchase")
      {
        VisitorId = "user-1",
        Url = "https://example.com/checkout",
        Dimensions = { ["plan"] = "pro" },
        Metrics = { ["revenue"] = 49.0 },
      });
      analytics.Track(new AnalyticsEvent("job_completed")
          .WithDimension("queue", "emails")
          .WithMetric("duration_ms", 812));
      await analytics.FlushAsync();

      // Web reports
      var request = new WebReportRequest { Range = DateRange.LastDays(30), Compare = Comparison.PreviousPeriod };
      var summary = await analytics.Web.GetSummaryAsync(request);
      var sources = await analytics.Web.GetBreakdownAsync(request, StandardDimensions.Source);
      var pages = await analytics.Web.GetPagesAsync(request);
      var goals = await analytics.Web.GetGoalsAsync(request);
      var german = new WebReportRequest { Filters = { Filter.Equal(StandardDimensions.Country, "DE") } };
      var germanSummary = await analytics.Web.GetSummaryAsync(german);

      // Queries
      var result = await analytics.QueryAsync(new AnalyticsQuery
      {
        Range = DateRange.LastDays(30),
        GroupBy = { StandardDimensions.Source },
        Metrics = { Metric.Visitors, Metric.BounceRate, Metric.ConversionRate("purchase") },
        OrderBy = { Order.By(Metric.Visitors) },
        Limit = 10,
      });
      var revenue = await analytics.QueryAsync(new AnalyticsQuery
      {
        Event = "purchase",
        Range = DateRange.ThisMonth,
        Interval = TimeInterval.Week,
        GroupBy = { "plan" },
        Metrics = { Metric.Sum("revenue"), Metric.Visitors },
      });
      var averageOrder = Metric.Sum("revenue") / Metric.Events;
      var monthToDate = Metric.Cumulative(Metric.Sum("revenue"));
      var daily = await analytics.QueryAsync(new AnalyticsQuery
      {
        Event = "purchase",
        Range = DateRange.ThisMonth,
        Interval = TimeInterval.Day,
        Metrics =
        {
          averageOrder,
          monthToDate,
          Metric.MovingAverage(Metric.Visitors, 7),
          Metric.Rate(Metric.Events, TimeSpan.FromHours(1)),
        },
      });
      var converted = await analytics.QueryAsync(new AnalyticsQuery
      {
        Segments = { Segment.VisitorsWho("purchase") },
        Metrics = { Metric.PageViews, Metric.Visitors },
      });
      var funnel = await analytics.QueryFunnelAsync(new FunnelQuery
      {
        Steps =
        {
          FunnelStep.Page("Pricing", "/pricing"),
          FunnelStep.Goal("purchase"),
        },
        Window = TimeSpan.FromDays(1),
      });

      Assert.Multiple(() =>
      {
        Assert.That(summary.PageViews, Is.EqualTo(1));
        Assert.That(summary.Visitors, Is.EqualTo(1));
        Assert.That(summary.Previous.PageViews, Is.EqualTo(0));
        Assert.That(sources.Rows.Single().Value, Is.EqualTo("Newsletter"));
        Assert.That(pages.Rows.Single().Page, Is.EqualTo("/pricing"));
        Assert.That(goals.Rows.Select(x => x.Name), Is.EquivalentTo(new[] { "purchase", "job_completed" }));
        Assert.That(germanSummary.PageViews, Is.EqualTo(1));
        Assert.That(result.Rows[0][StandardDimensions.Source], Is.EqualTo("Newsletter"));
        Assert.That(revenue.Rows.Where(x => x["plan"] == "pro").Sum(x => x[Metric.Sum("revenue")]), Is.EqualTo(49));
        Assert.That(daily.Totals[averageOrder], Is.EqualTo(49));
        Assert.That(daily.Rows[^1][monthToDate], Is.EqualTo(49));
        Assert.That(converted.Totals[Metric.PageViews], Is.EqualTo(0));
        Assert.That(funnel.Steps.Select(x => x.Visitors), Is.EqualTo(new long[] { 1, 0 }));
        Assert.That(analytics.Web.GetCurrentVisitors(), Is.EqualTo(2));
      });
    }
    finally
    {
      TestEngine.Delete(directory);
    }
  }
}
