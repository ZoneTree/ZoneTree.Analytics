namespace ZoneTree.Analytics.UnitTests;

public sealed class QueryGroupLimitTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-2);

  AnalyticsEngine Create(int maximum = 10, int parallelism = 1) => TestEngine.Create(directory, o =>
  {
    o.Sessions.Enabled = false;
    o.Queries.MaxGroups = maximum;
    o.Queries.MaxDegreeOfParallelism = parallelism;
    o.Rollups.Granularities = Granularity.Hour;
    o.Rollups.Definitions.Clear();
    o.Rollups.Definitions.Add(new RollupDefinition());
    o.Rollups.Definitions.Add(new RollupDefinition("group"));
  });

  static void Track(AnalyticsEngine engine, int groups, string name = "purchase")
  {
    for (var i = 0; i < groups; ++i)
      engine.Track(new AnalyticsEvent(name) { Timestamp = Utc(Day0.AddMinutes(15 + i * 30)), Dimensions = { ["group"] = "g" + i } });
    engine.Flush();
  }

  static AnalyticsQuery Grouped(QuerySource source, int? limit = null) => new()
  {
    Event = "purchase", Range = Day(Day0), Source = source, Limit = limit,
    GroupBy = { "group" }, Metrics = { Metric.Events }
  };

  [TestCase(QuerySource.RawEvents, 1)]
  [TestCase(QuerySource.RawEvents, 4)]
  [TestCase(QuerySource.Rollups, 1)]
  public void GroupBudgetIsEnforcedBeforePagingAndDoesNotPoisonLaterQueries(QuerySource source, int parallelism)
  {
    using var engine = Create(parallelism: parallelism);
    Track(engine, 20);
    var query = Grouped(source, limit: 1);
    query.Having.Add(MetricFilter.GreaterThan(Metric.Events, 100));
    var error = Assert.Throws<AnalyticsQueryException>(() => engine.Query(query));
    Assert.That(error.Message, Does.Contain("Queries.MaxGroups"));
    var totals = engine.Query(new AnalyticsQuery { Event = "purchase", Range = Day(Day0), Source = source, Metrics = { Metric.Events } });
    Assert.That(totals.Totals[Metric.Events], Is.EqualTo(20));
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void ExactlyTheConfiguredNumberOfGroupsCanBeRead(QuerySource source)
  {
    using var engine = Create();
    Track(engine, 10);
    var result = engine.Query(Grouped(source, limit: 1));
    Assert.That(result.TotalRows, Is.EqualTo(10));
    Assert.That(result.Rows, Has.Count.EqualTo(1));
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(10));
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void DifferentSourcesShareTheBudget(QuerySource source)
  {
    using var engine = Create();
    Track(engine, 6);
    Track(engine, 6, "signup");
    var query = Grouped(source);
    query.Metrics.Add(Metric.EventsOf("signup"));
    Assert.Throws<AnalyticsQueryException>(() => engine.Query(query));
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void DifferentTimeBucketsCountAsDifferentGroups(QuerySource source)
  {
    using var engine = Create();
    for (var hour = 0; hour < 20; ++hour)
      engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(Day0.AddHours(hour).AddMinutes(15)) });
    engine.Flush();
    var query = new AnalyticsQuery { Event = "purchase", Range = Day(Day0), Source = source, Interval = TimeInterval.Hour, Fill = FillMode.None, LimitPerBucket = 1, Metrics = { Metric.Events } };
    Assert.Throws<AnalyticsQueryException>(() => engine.Query(query));
  }

  [Test]
  public void MergedPartialGroupsReleaseTheirBudgetBeforeTheNextRawRange()
  {
    using var engine = Create(maximum: 2);
    foreach (var minutes in new[] { 15, 12 * 60 + 15, 23 * 60 + 45 })
      engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(Day0.AddMinutes(minutes)), Dimensions = { ["group"] = "same" } });
    engine.Flush();
    var query = Grouped(QuerySource.Auto);
    query.Range = Range(Day0.AddMinutes(10), Day0.AddHours(23).AddMinutes(55));
    var result = engine.Query(query);
    Assert.That(result.Plan.UsedRawEvents, Is.True);
    Assert.That(result.Plan.Steps.Any(step => step.Source == PlanSource.HourRollups), Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(3));
  }

  [Test]
  public async Task ConcurrentQueriesHaveIndependentBudgets()
  {
    using var engine = Create(maximum: 6);
    Track(engine, 6);
    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => engine.QueryAsync(Grouped(QuerySource.RawEvents))));
    Assert.That(results.All(result => result.TotalRows == 6 && result.Totals[Metric.Events] == 6), Is.True);
  }

  [TestCase(0)]
  [TestCase(-1)]
  public void GroupLimitMustBePositive(int maximum) =>
      Assert.Throws<ArgumentException>(() => TestEngine.Create(directory, o => o.Queries.MaxGroups = maximum));
}
