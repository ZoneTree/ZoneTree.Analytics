using System.Globalization;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Rollups rebuilt from raw events and queries of raw events derive session metrics from the
/// same reconstructed sessions, so they must agree with each other and with live ingestion.
/// </summary>
public sealed class SessionRollupConsistencyTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-3);

  // Session metrics group by session-level dimensions; page metrics by page.
  static readonly (string[] GroupBy, Metric[] Metrics)[] Queries =
  [
    ([StandardDimensions.Source, StandardDimensions.EntryPage],
        [Metric.Sessions, Metric.Bounces, Metric.BounceRate, Metric.SessionDuration, Metric.PageViewsPerSession]),
    ([StandardDimensions.Source, StandardDimensions.Page], [Metric.Entrances, Metric.Exits, Metric.PageViews])
  ];

  static void Ingest(AnalyticsEngine engine)
  {
    for (var visitor = 0; visitor < 30; ++visitor)
    {
      // Every third visitor bounces; the others view three or four pages, minutes apart.
      var pages = visitor % 3 == 0 ? 1 : 2 + visitor % 3;
      var start = Day0.AddHours(1 + visitor % 20).AddMinutes(visitor);
      for (var page = 0; page < pages; ++page)
      {
        Assert.That(engine.Track(new AnalyticsEvent(StandardEvents.PageView)
        {
          VisitorId = "v" + visitor, Url = "/p" + (visitor + page) % 4, Timestamp = Utc(start.AddMinutes(page * 5)),
          Dimensions = { [StandardDimensions.Source] = visitor % 2 == 0 ? "GitHub" : "Google" }
        }), Is.True);
      }
    }
    engine.Flush();
  }

  static (AnalyticsResult Result, string[] Rows) Run(AnalyticsEngine engine, QuerySource source, string[] groupBy, Metric[] metrics)
  {
    var query = new AnalyticsQuery { Range = Day(Day0), Source = source };
    query.GroupBy.AddRange(groupBy);
    query.Metrics.AddRange(metrics);
    var result = engine.Query(query);
    return (result, [.. result.Rows.Select(row =>
        string.Join("|", groupBy.Select(dimension => row[dimension])) + "|" +
        string.Join(",", metrics.Select(metric => row[metric].ToString("R", CultureInfo.InvariantCulture))))]);
  }

  [Test]
  public void RebuiltRollupsRawQueriesAndLiveIngestionAgreeOnSessionMetrics()
  {
    using (var engine = TestEngine.Create(directory))
      Ingest(engine);
    // Rollup indexes added later rebuild their joint rollups from the retained raw events.
    using var rebuilt = TestEngine.Create(directory, o =>
    {
      o.Storage.RollupIndexes.Definitions.Add(new(StandardDimensions.Source, StandardDimensions.EntryPage));
      o.Storage.RollupIndexes.Definitions.Add(new(StandardDimensions.Source, StandardDimensions.Page));
    });
    using var live = TestEngine.Create(Path.Combine(directory, "live"), o =>
    {
      o.Rollups.Definitions.Add(new RollupDefinition(StandardDimensions.EntryPage, StandardDimensions.Source));
      o.Rollups.Definitions.Add(new RollupDefinition(StandardDimensions.Page, StandardDimensions.Source));
    });
    Ingest(live);
    foreach (var (groupBy, metrics) in Queries)
    {
      var fromRollups = Run(rebuilt, QuerySource.Rollups, groupBy, metrics);
      var fromRaw = Run(rebuilt, QuerySource.RawEvents, groupBy, metrics);
      var fromLive = Run(live, QuerySource.Rollups, groupBy, metrics);
      var name = string.Join(",", groupBy);
      Assert.Multiple(() =>
      {
        Assert.That(fromRollups.Result.Plan.Steps.All(step => step.Source != PlanSource.RawEvents), Is.True, name + ": answered by the rebuilt rollup");
        Assert.That(fromRollups.Rows, Is.Not.Empty, name);
        Assert.That(fromRollups.Rows, Is.EquivalentTo(fromRaw.Rows), name + ": rebuilt rollups and raw queries");
        Assert.That(fromLive.Rows, Is.EquivalentTo(fromRaw.Rows), name + ": live ingestion and raw queries");
      });
    }
  }
}
