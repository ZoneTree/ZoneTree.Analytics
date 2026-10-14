using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class WebReportProjectionTests : DirectoryTest
{
  [TestCase("raw")]
  [TestCase("index")]
  [TestCase("rollup")]
  public void OverviewMetricsPreserveCountsComparisonsAndPagingWithoutSessionSources(string storage)
  {
    using var engine = TestEngine.Create(directory, options =>
    {
      options.Queries.MaxDegreeOfParallelism = 2;
      if (storage != "rollup")
        options.Rollups.Definitions.Clear();
      else
        foreach (var dimension in new[] { "country", "device", "page" })
          options.Rollups.Definitions.Add(new("source", dimension));
      if (storage == "index")
        options.Storage.DimensionIndexes.Dimensions.Add("source");
    });
    var start = DateTime.UtcNow.Date.AddDays(-5);
    void Track(string visitor, DateTime at, string page, string country, string source = "GitHub", string name = "pageview")
    {
      Assert.That(engine.Track(new AnalyticsEvent(name)
      {
        VisitorId = visitor, Timestamp = Utc(at), Url = page, UserAgent = UserAgents.ChromeWindows,
        Dimensions = { ["source"] = source, ["country"] = country }, Values = { ["load_time"] = 120 }
      }), Is.True);
    }
    Track("a", start, "/docs", "DE");
    Track("a", start.AddMinutes(1), "/pricing", "DE");
    Track("a", start.AddMinutes(2), "/pricing", "DE", name: "signup");
    Track("b", start.AddHours(1), "/docs", "US");
    Track("b", start.AddHours(1).AddMinutes(2), "/", "US");
    Track("excluded", start, "/excluded", "DE", "Google");
    Track("previous", start.AddDays(-1), "/docs", "DE");
    engine.Flush();
    var request = new WebReportRequest
    {
      Range = Day(start), Compare = ComparisonPeriod.PreviousPeriod,
      Filters = { Filter.Equal("source", "GitHub") }, Limit = 1
    };
    foreach (var dimension in new[] { "country", "device" })
    {
      request.IncludeSessionMetrics = true;
      var full = engine.Web.GetBreakdown(request, dimension);
      request.IncludeSessionMetrics = false;
      var projected = engine.Web.GetBreakdown(request, dimension);
      Assert.That(projected.TotalRows, Is.EqualTo(full.TotalRows));
      Assert.That(projected.Rows.Count, Is.EqualTo(1));
      Assert.That(full.Rows[0].Sessions, Is.Not.Null);
      Assert.That(projected.Rows, Is.EqualTo(full.Rows.Select(row => row with
      {
        Sessions = null, BounceRate = null, AverageSessionDuration = null,
        Previous = row.Previous == null ? null : row.Previous with
        { Sessions = null, BounceRate = null, AverageSessionDuration = null }
      })));
      Assert.That(projected.Plan.Steps.All(step => step.Event == StandardEvents.PageView), Is.True);
      Assert.That(projected.Plan.UsedDimensionIndexes, Is.EqualTo(storage == "index"));
      Assert.That(projected.Plan.UsedRollups, Is.EqualTo(storage == "rollup"));
    }
    static WebSummary WithoutSessions(WebSummary summary) => summary with
    {
      Sessions = null, BounceRate = null, AverageSessionDuration = null, PageViewsPerSession = null,
      Previous = summary.Previous == null ? null : WithoutSessions(summary.Previous)
    };
    request.IncludeSessionMetrics = true;
    var fullSummary = engine.Web.GetSummary(request);
    var fullSeries = engine.Web.GetTimeseries(request, TimeInterval.Hour);
    request.IncludeSessionMetrics = false;
    var projectedSummary = engine.Web.GetSummary(request);
    var projectedSeries = engine.Web.GetTimeseries(request, TimeInterval.Hour);
    Assert.That(projectedSummary.Summary, Is.EqualTo(WithoutSessions(fullSummary.Summary)));
    Assert.That(projectedSeries.Rows, Is.EqualTo(fullSeries.Rows.Select(row => row with { Summary = WithoutSessions(row.Summary) })));
    Assert.That(projectedSummary.Plan.Steps.All(step => step.Event == StandardEvents.PageView), Is.True);
    Assert.That(projectedSeries.Plan.Steps.All(step => step.Event == StandardEvents.PageView), Is.True);
    foreach (var offset in new[] { 0, 1, 3 })
    {
      request.Offset = offset;
      request.IncludePageFlowMetrics = true;
      var fullPages = engine.Web.GetPages(request);
      request.IncludePageFlowMetrics = false;
      var projected = engine.Web.GetPages(request);
      Assert.That(projected.TotalRows, Is.EqualTo(fullPages.TotalRows));
      Assert.That(projected.Rows, Is.EqualTo(fullPages.Rows.Select(row => row with
      {
        Entrances = null, Exits = null, ExitRate = null,
        Previous = row.Previous == null ? null : row.Previous with { Entrances = null, Exits = null, ExitRate = null }
      })));
      Assert.That(projected.Plan.UsedDimensionIndexes, Is.EqualTo(storage == "index"));
      Assert.That(projected.Plan.UsedRollups, Is.EqualTo(storage == "rollup"));
    }
    Assert.That(() => engine.Web.GetExitPages(request), Throws.ArgumentException.With.Message.Contains("page-flow"));
  }

  [Test]
  public void PageOnlyMetricsAllowAnEventScopedCoveringIndex()
  {
    using var engine = TestEngine.Create(directory, options =>
    {
      options.Rollups.Definitions.Clear();
      options.Storage.DimensionIndexes.Dimensions.Add("page");
      options.Queries.MaxDegreeOfParallelism = 2;
    });
    var start = DateTime.UtcNow.Date.AddDays(-5);
    foreach (var page in new[] { "/docs", "/pricing" })
      engine.Track(new AnalyticsEvent("pageview") { VisitorId = "a", Timestamp = Utc(start), Url = page });
    engine.Flush();
    var request = new WebReportRequest { Range = Day(start), Filters = { Filter.Equal("page", "/docs") } };
    var full = engine.Web.GetPages(request);
    Assert.That(full.Plan.UsedDimensionIndexes, Is.False, "Full page flow needs the other page's events too.");
    request.IncludePageFlowMetrics = false;
    var projected = engine.Web.GetPages(request);
    Assert.That(projected.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(projected.Rows.Single().PageViews, Is.EqualTo(full.Rows.Single().PageViews));
    Assert.That(projected.Rows.Single().Exits, Is.Null);
  }
}
