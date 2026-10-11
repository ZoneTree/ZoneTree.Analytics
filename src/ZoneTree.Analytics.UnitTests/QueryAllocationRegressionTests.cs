namespace ZoneTree.Analytics.UnitTests;

public sealed class QueryAllocationRegressionTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-2);

  AnalyticsEngine CreateEngine(int parallelism = 4)
  {
    var engine = TestEngine.Create(directory, o =>
    {
      o.Queries.MaxDegreeOfParallelism = parallelism;
      o.Rollups.Definitions.Clear();
      foreach (var definition in new[]
      {
        new RollupDefinition(), new(StandardDimensions.Hostname), new(StandardDimensions.Source),
        new(StandardDimensions.Hostname, StandardDimensions.Source),
        new(StandardDimensions.Page, StandardDimensions.Hostname),
        new(StandardDimensions.Page, StandardDimensions.Hostname, StandardDimensions.Source)
      })
        o.Rollups.Definitions.Add(definition);
    });
    foreach (var (visitor, host, source) in new[]
    {
      ("a", "one.example", (string)null), ("b", "two.example", (string)null),
      ("c", "one.example", "https://www.google.com/")
    })
    {
      engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = visitor, Url = $"https://{host}/entry-{visitor}", Referrer = source, Timestamp = Utc(Day0.AddMinutes(1))
      });
      engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = visitor, Url = $"https://{host}/exit-{visitor}", Timestamp = Utc(Day0.AddMinutes(6))
      });
    }
    engine.Flush();
    return engine;
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void PreparedFiltersPreserveGlobalAndConditionalSemantics(QuerySource source)
  {
    using var engine = CreateEngine();
    var conditionalViews = Metric.PageViews.Where(Filter.Equal(StandardDimensions.Hostname, "one.example"));
    foreach (var global in new[] { false, true })
    {
      foreach (var conditional in new[] { false, true })
      {
        var query = new AnalyticsQuery { Range = Day(Day0), Source = source, Metrics = { Metric.PageViews } };
        if (global)
          query.Filters.Add(Filter.Equal(StandardDimensions.Source, "Direct"));
        if (conditional)
          query.Metrics.Add(conditionalViews);
        var result = engine.Query(query);
        Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(global ? 4 : 6));
        if (conditional)
          Assert.That(result.Totals[conditionalViews], Is.EqualTo(global ? 2 : 4));
      }
    }
  }

  [TestCase(QuerySource.RawEvents, 1)]
  [TestCase(QuerySource.RawEvents, 4)]
  [TestCase(QuerySource.Rollups, 1)]
  [TestCase(QuerySource.Rollups, 4)]
  public void PageOverlaysDoNotLeakBetweenSessionsOrConditionalSources(QuerySource source, int parallelism)
  {
    using var engine = CreateEngine(parallelism);
    var exitsOnOne = Metric.Exits.Where(Filter.Equal(StandardDimensions.Hostname, "one.example"));
    var entrancesOnTwo = Metric.Entrances.Where(Filter.Equal(StandardDimensions.Hostname, "two.example"));
    var result = engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Source = source, GroupBy = { StandardDimensions.Page, StandardDimensions.Hostname },
      Filters = { Filter.Equal(StandardDimensions.Source, "Direct") },
      Metrics = { Metric.PageViews, Metric.Entrances, Metric.Exits, exitsOnOne, entrancesOnTwo }
    });
    Assert.That(result.Rows.Count, Is.EqualTo(4));
    foreach (var row in result.Rows)
    {
      var page = row[StandardDimensions.Page];
      var entrance = page.StartsWith("/entry-", StringComparison.Ordinal);
      var onOne = page.EndsWith('a');
      Assert.Multiple(() =>
      {
        Assert.That(row[StandardDimensions.Hostname], Is.EqualTo(onOne ? "one.example" : "two.example"));
        Assert.That(row[Metric.PageViews], Is.EqualTo(1));
        Assert.That(row[Metric.Entrances], Is.EqualTo(entrance ? 1 : 0));
        Assert.That(row[Metric.Exits], Is.EqualTo(entrance ? 0 : 1));
        Assert.That(row[exitsOnOne], Is.EqualTo(!entrance && onOne ? 1 : 0));
        Assert.That(row[entrancesOnTwo], Is.EqualTo(entrance && !onOne ? 1 : 0));
      });
    }
  }
}
