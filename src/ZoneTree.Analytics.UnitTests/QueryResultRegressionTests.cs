namespace ZoneTree.Analytics.UnitTests;

public sealed class QueryResultRegressionTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-3);

  AnalyticsEngine CreateEngine() => TestEngine.Create(directory, o =>
      o.Rollups.Definitions.Add(new RollupDefinition("room")));

  static void Track(AnalyticsEngine engine, DateTime time, string room, double amount = 1) =>
      engine.Track(new AnalyticsEvent("reading")
      {
        VisitorId = "visitor", Timestamp = Utc(time),
        Dimensions = { ["room"] = room }, Values = { ["amount"] = amount }
      });

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void HavingEvaluatesNestedSeriesMetricsThatAreNotSelected(QuerySource source)
  {
    using var engine = CreateEngine();
    foreach (var (room, hour, count) in new[] { ("a", 0, 2), ("a", 2, 3), ("b", 0, 1), ("b", 2, 1) })
      for (var i = 0; i < count; ++i)
        Track(engine, Day0.AddHours(hour), room);
    engine.Flush();
    var running = Metric.Cumulative(Metric.Events.Where(Filter.Equal("room", "a"))) + 1;
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "reading", Range = Range(Day0, Day0.AddHours(3)), Source = source,
      Interval = TimeInterval.Hour, Fill = FillMode.Zero,
      GroupBy = { "room" }, Metrics = { Metric.Events },
      Having = { MetricFilter.GreaterThanOrEqual(running, 5) }
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Rows, Has.Count.EqualTo(1));
      Assert.That(result.Rows.Single()["room"], Is.EqualTo("a"));
      Assert.That(result.Rows.Single().Bucket, Is.EqualTo(Utc(Day0.AddHours(2))));
      Assert.That(result.Rows.Single()[Metric.Events], Is.EqualTo(3));
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(7));
    });
  }

  [TestCase(QuerySource.RawEvents, false)]
  [TestCase(QuerySource.RawEvents, true)]
  [TestCase(QuerySource.Rollups, false)]
  [TestCase(QuerySource.Rollups, true)]
  public void ComparisonMatchesSparseBucketsAfterHavingSortingAndPaging(QuerySource source, bool hourly)
  {
    var interval = hourly ? TimeInterval.Hour : TimeInterval.None;
    using var engine = CreateEngine();
    foreach (var (room, hour, amount) in new[] { ("a", 0, 10), ("b", 1, 20), ("a", 2, 30) })
    {
      Track(engine, Day0.AddHours(hour), room, amount);
      Track(engine, Day0.AddDays(-1).AddHours(hour), room, amount / 10.0);
    }
    engine.Flush();
    var sum = Metric.Sum("amount");
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "reading", Range = Range(Day0, Day0.AddHours(3)), Source = source,
      Interval = interval, Fill = FillMode.None,
      Compare = ComparisonPeriod.Custom(Range(Day0.AddDays(-1), Day0.AddDays(-1).AddHours(3))),
      GroupBy = { "room" }, Metrics = { sum }, Having = { MetricFilter.GreaterThanOrEqual(sum, 15) },
      OrderBy = { Order.By(sum) }, Offset = 1, Limit = 1
    });
    var row = result.Rows.Single();
    Assert.Multiple(() =>
    {
      Assert.That(result.TotalRows, Is.EqualTo(2));
      Assert.That(row["room"], Is.EqualTo("b"));
      Assert.That(row[sum], Is.EqualTo(20));
      Assert.That(row.Previous, Is.Not.Null);
      Assert.That(row.Previous["room"], Is.EqualTo("b"));
      Assert.That(row.Previous[sum], Is.EqualTo(2));
      Assert.That(row.Previous.Bucket, Is.EqualTo(interval == TimeInterval.None ? (DateTimeOffset?)null : Utc(Day0.AddDays(-1).AddHours(1))));
      Assert.That(result.Totals[sum], Is.EqualTo(60));
      Assert.That(result.Totals.Previous[sum], Is.EqualTo(6));
      Assert.That(result.Comparison.Rows, Has.Count.EqualTo(interval == TimeInterval.None ? 2 : 3));
    });
  }

  [TestCase(QuerySource.RawEvents, false)]
  [TestCase(QuerySource.RawEvents, true)]
  [TestCase(QuerySource.Rollups, false)]
  [TestCase(QuerySource.Rollups, true)]
  public void PreparedSortColumnsPreserveDirectionsMissingValuesAndTies(QuerySource source, bool descending)
  {
    using var engine = CreateEngine();
    foreach (var (room, hour, amount) in new[]
    {
      ((string)null, 0, 2), ("a", 0, 2), ("b", 0, 1),
      ((string)null, 1, 2), ("a", 1, 2), ("b", 1, 3)
    })
      Track(engine, Day0.AddHours(hour), room, amount);
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "reading", Range = Range(Day0, Day0.AddHours(2)), Source = source,
      Interval = TimeInterval.Hour, Fill = FillMode.None, GroupBy = { "room" },
      Metrics = { Metric.Events, Metric.Sum("amount") },
      // Equivalent metric instances must resolve to the same column; the sum is column 1.
      OrderBy = { null, Order.By(Metric.Sum("amount"), descending), Order.ByDimension("room", descending) }
    });
    var actual = result.Rows.Select(row => (row["room"], row.Bucket)).ToArray();
    var expected = descending
        ? new[] { ("b", Utc(Day0.AddHours(1))), ("a", Utc(Day0)), ("a", Utc(Day0.AddHours(1))),
          ((string)null, Utc(Day0)), ((string)null, Utc(Day0.AddHours(1))), ("b", Utc(Day0)) }
        : new[] { ("b", Utc(Day0)), ((string)null, Utc(Day0)), ((string)null, Utc(Day0.AddHours(1))),
          ("a", Utc(Day0)), ("a", Utc(Day0.AddHours(1))), ("b", Utc(Day0.AddHours(1))) };
    Assert.That(actual, Is.EqualTo(expected.Select(x => (x.Item1, (DateTimeOffset?)x.Item2))));
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void DirectLookupsPreserveConditionalConversionRatesAndMissingSources(QuerySource source)
  {
    using var engine = CreateEngine();
    foreach (var (room, views, goals) in new[] { ("a", 3, 1), ("b", 2, 1), ("c", 0, 1) })
    {
      foreach (var (eventName, count) in new[] { ("reading", views), ("signup", goals) })
        for (var i = 0; i < count; ++i)
          engine.Track(new AnalyticsEvent(eventName)
          {
            VisitorId = room + i, Timestamp = Utc(Day0), Dimensions = { ["room"] = room }
          });
    }
    engine.Flush();
    var rate = Metric.ConversionRate("signup");
    var conditionalRate = rate.Where(Filter.Equal("room", "a"));
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "reading", Range = Day(Day0), Source = source, GroupBy = { "room" },
      Metrics = { Metric.Events, rate, conditionalRate }
    });
    var a = result.Rows.Single(x => x["room"] == "a");
    var b = result.Rows.Single(x => x["room"] == "b");
    var c = result.Rows.Single(x => x["room"] == "c");
    Assert.Multiple(() =>
    {
      Assert.That(a[rate], Is.EqualTo(1.0 / 3).Within(1e-9));
      Assert.That(a[conditionalRate], Is.EqualTo(1.0 / 3).Within(1e-9));
      Assert.That(b[rate], Is.EqualTo(0.5).Within(1e-9));
      Assert.That(b[conditionalRate], Is.Zero);
      Assert.That(c[Metric.Events], Is.Zero);
      Assert.That(c[rate], Is.Zero);
      Assert.That(c[conditionalRate], Is.Zero);
      Assert.That(result.Totals[rate], Is.EqualTo(3.0 / 5).Within(1e-9));
      Assert.That(result.Totals[conditionalRate], Is.EqualTo(1.0 / 3).Within(1e-9));
    });
  }
}
