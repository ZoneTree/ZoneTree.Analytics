namespace ZoneTree.Analytics.UnitTests;

public sealed class ImplementationReviewTests : DirectoryTest
{
  [Test]
  public void MalformedUnicodeInFilterGroupsUsesTheParserErrorContract()
  {
    var expression = """any("page==\uD800")""";
    Assert.Throws<FormatException>(() => Filter.Parse(expression));
    Assert.That(Filter.TryParse(expression, out _), Is.False);
    var invalid = ((char)0xd800).ToString();
    Assert.Throws<ArgumentException>(() => Filter.Equal(invalid, "value"));
    Assert.Throws<ArgumentException>(() => Filter.Equal("page", invalid));
    Assert.Throws<FormatException>(() => Filter.Parse("page==" + invalid));
  }

  [Test]
  public void CustomDstTransitionBeforeBucketBoundaryPreservesHourGroups()
  {
    var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1);
    var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 59, 59, 999), 11, 1);
    var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31),
        TimeSpan.FromHours(1), start, end);
    var zone = TimeZoneInfo.CreateCustomTimeZone("sub-bucket-dst", TimeSpan.Zero, "sub-bucket-dst", "standard", "daylight", [rule]);
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Sessions.Enabled = false;
      o.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
    });
    var day = new DateTime(2025, 11, 1);
    engine.Track(new AnalyticsEvent("test") { Timestamp = Utc(day.AddMinutes(30)) });
    engine.Track(new AnalyticsEvent("test") { Timestamp = Utc(day.AddHours(1).AddMilliseconds(-1)) });
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Event = "test", Range = Day(day), TimeZone = zone,
      GroupBy = { StandardDimensions.HourOfDay }, Metrics = { Metric.Events }, Source = QuerySource.RawEvents
    };
    var raw = engine.Query(query);
    Assert.That(raw.Rows.ToDictionary(x => x[StandardDimensions.HourOfDay], x => x[Metric.Events]),
        Is.EquivalentTo(new Dictionary<string, double> { ["0"] = 1, ["1"] = 1 }));
    query.Source = QuerySource.Auto;
    var automatic = engine.Query(query);
    Assert.That(automatic.Rows.ToDictionary(x => x[StandardDimensions.HourOfDay], x => x[Metric.Events]),
        Is.EquivalentTo(raw.Rows.ToDictionary(x => x[StandardDimensions.HourOfDay], x => x[Metric.Events])));
  }

  [TestCase(StandardDimensions.HourOfDay)]
  [TestCase(StandardDimensions.DayOfWeek)]
  public void StoredDistinctSketchDoesNotPolluteQueryTimeDistinctCount(string dimension)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Sessions.Enabled = false;
      o.Rollups.DistinctDimensions.Add(dimension);
    });
    var day = DateTime.UtcNow.Date.AddDays(-2);
    engine.Track(new AnalyticsEvent("test")
    {
      Timestamp = Utc(day.AddHours(9)), Dimensions = { [dimension] = "17" }
    });
    engine.Flush();
    var distinct = Metric.Distinct(dimension);
    var query = new AnalyticsQuery { Event = "test", Range = Day(day), Metrics = { distinct }, Source = QuerySource.RawEvents };
    var raw = engine.Query(query);
    Assert.That(raw.Totals[distinct], Is.EqualTo(1));
    query.Source = QuerySource.Rollups;
    Assert.That(engine.Query(query).Totals[distinct], Is.EqualTo(raw.Totals[distinct]));
  }
}
