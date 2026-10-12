using System.Globalization;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class FilterGroupTests
{
  [Test]
  public void GroupsPreserveMissingValueSemanticsAndRoundTripNestedExpressions()
  {
    var filter = Filter.AnyOf(Filter.Equal("country", "DE"), Filter.AllOf(
        Filter.IsNotSet("country"), Filter.Equal("tag", "x;y,\"z\\|")));
    var parsed = Filter.Parse(filter.ToString());
    var conditional = Metric.Events.Where(filter);
    Assert.That(Metric.Parse(conditional.ToString()), Is.EqualTo(conditional));
    Assert.Multiple(() =>
    {
      Assert.That(parsed.ToString(), Is.EqualTo(filter.ToString()));
      Assert.That(parsed.Matches(new Dictionary<string, string> { ["country"] = "DE" }), Is.True);
      Assert.That(parsed.Matches(new Dictionary<string, string> { ["tag"] = "x;y,\"z\\|" }), Is.True);
      Assert.That(parsed.Matches(new Dictionary<string, string> { ["country"] = "US", ["tag"] = "x;y,\"z\\|" }), Is.False);
      Assert.That(parsed.Dimensions, Is.EquivalentTo(new[] { "country", "tag" }));
      Assert.That(parsed.Dimension, Is.Null);
      Assert.That(Filter.AllOf(Filter.NotEqual("country", "DE"), Filter.IsNotSet("page"))
          .Matches(new Dictionary<string, string>()), Is.True);
    });
    var segment = Segment.VisitorsWho("purchase", filter, Filter.IsSet("tag"));
    Assert.That(Segment.Parse(segment.ToString()).ToString(), Is.EqualTo(segment.ToString()));
  }

  [TestCase("any()")]
  [TestCase("any(country==DE)")]
  [TestCase("any(\"country==DE\",)")]
  [TestCase("any(\"country==DE\"")]
  [TestCase("all(null)")]
  [TestCase("all(\"\")")]
  public void MalformedGroupsAreRejected(string text)
  {
    Assert.Throws<FormatException>(() => Filter.Parse(text));
    Assert.That(Filter.TryParse(text, out _), Is.False);
  }

  [Test]
  public void GroupsAreImmutableAndHaveBoundedComplexity()
  {
    var items = new[] { Filter.Equal("a", "x") };
    var group = Filter.AnyOf(items);
    items[0] = Filter.Equal("a", "y");
    Assert.That(group.Matches(new Dictionary<string, string> { ["a"] = "x" }), Is.True);
    Assert.Throws<ArgumentException>(() => Filter.AnyOf());
    Assert.Throws<ArgumentNullException>(() => Filter.AnyOf(null));
    Assert.Throws<ArgumentException>(() => Filter.AnyOf(items[0], null));
    Assert.Throws<ArgumentException>(() => Filter.AllOf(Enumerable.Repeat(items[0], 1025).ToArray()));
    for (var i = 0; i < 30; ++i)
      group = Filter.AllOf(group);
    Assert.That(Filter.Parse(group.ToString()).ToString(), Is.EqualTo(group.ToString()));
    Assert.Throws<ArgumentException>(() => Filter.AllOf(group));
    Assert.Throws<FormatException>(() => Filter.Parse("all(" + group + ")"));
  }
}

public sealed class QueryFeatureTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-3);

  AnalyticsEngine Create(Action<AnalyticsOptions> configure = null) => TestEngine.Create(directory, o =>
  {
    o.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
    o.Rollups.Definitions.Add(new RollupDefinition("page", "country"));
    configure?.Invoke(o);
  });

  static void Track(AnalyticsEngine engine, DateTime timestamp, string visitor, string page, string country = null, string name = "pageview")
  {
    var item = new AnalyticsEvent(name) { Timestamp = Utc(timestamp), VisitorId = visitor, Url = "https://example.com" + page };
    if (country != null)
      item.Dimensions["country"] = country;
    engine.Track(item);
  }

  static Filter Selection() => Filter.AnyOf(Filter.Equal("country", "DE"),
      Filter.AllOf(Filter.Equal("page", "/pricing"), Filter.NotEqual("country", "FR")));

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  [TestCase(QuerySource.Auto)]
  public void OrFiltersAndConditionalMetricsCountOverlappingBranchesOnce(QuerySource source)
  {
    using var engine = Create();
    Track(engine, Day0.AddHours(9), "a", "/", "DE");
    Track(engine, Day0.AddHours(10), "b", "/pricing", "DE");
    Track(engine, Day0.AddHours(11), "c", "/pricing");
    Track(engine, Day0.AddHours(12), "d", "/pricing", "FR");
    Track(engine, Day0.AddHours(13), "e", "/", "US");
    engine.Flush();
    var selected = Metric.Events.Where(Selection());
    var query = new AnalyticsQuery { Range = Day(Day0), Source = source, Metrics = { Metric.Events, selected } };
    var result = engine.Query(query);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(5));
    Assert.That(result.Totals[selected], Is.EqualTo(3));
    query.Filters.Add(Selection());
    query.GroupBy.Add("country");
    result = engine.Query(query);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(3));
    Assert.That(result.Rows.Single(x => x["country"] == "")[Metric.Events], Is.EqualTo(1));
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void TimeDimensionsGroupFilterAndCountDistinctWithoutStoredDimensions(QuerySource source)
  {
    using var engine = Create();
    Track(engine, Day0.AddMinutes(15), "a", "/");
    Track(engine, Day0.AddHours(9), "b", "/");
    Track(engine, Day0.AddHours(9).AddMinutes(40), "c", "/");
    Track(engine, Day0.AddDays(1).AddHours(9), "d", "/");
    engine.Flush();
    var distinctHours = Metric.Distinct(StandardDimensions.HourOfDay);
    var conditional = Metric.Events.Where(Filter.Equal(StandardDimensions.HourOfDay, "9"));
    var query = new AnalyticsQuery
    {
      Range = Range(Day0, Day0.AddDays(2)), Source = source,
      GroupBy = { StandardDimensions.HourOfDay, StandardDimensions.DayOfWeek },
      Metrics = { Metric.Events, distinctHours, conditional }
    };
    var result = engine.Query(query);
    Assert.That(result.Rows, Has.Count.EqualTo(3));
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(4));
    Assert.That(result.Totals[distinctHours], Is.EqualTo(2));
    Assert.That(result.Totals[conditional], Is.EqualTo(3));
    if (source == QuerySource.Rollups)
      Assert.That(result.Plan.Steps.All(x => x.Source == PlanSource.HourRollups), Is.True);
    query.Filters.Add(Filter.Equal(StandardDimensions.HourOfDay, "9"));
    Assert.That(engine.Query(query).Totals[Metric.Events], Is.EqualTo(3));
    query.Interval = TimeInterval.Day;
    query.Fill = FillMode.None;
    var series = engine.Query(query);
    Assert.That(series.Rows, Has.Count.EqualTo(2));
    Assert.That(series.Rows.All(x => x[StandardDimensions.HourOfDay] == "9"), Is.True);
    Assert.Throws<ArgumentException>(() => new RollupDefinition(StandardDimensions.HourOfDay));
  }

  [TestCase("America/New_York", 2025, 3, 9)]
  [TestCase("America/New_York", 2025, 11, 2)]
  [TestCase("Asia/Kolkata", 2025, 6, 1)]
  [TestCase("Asia/Kathmandu", 2025, 6, 1)]
  [TestCase("Australia/Lord_Howe", 2025, 10, 5)]
  public void TimeDimensionsAgreeAcrossRawAndRollupsAtOffsetAndDstBoundaries(string zoneId, int year, int month, int day)
  {
    using var engine = Create();
    var from = new DateTime(year, month, day).AddDays(-1);
    for (var i = 0; i < 48 * 4; ++i)
      Track(engine, from.AddMinutes(i * 15 + 7), "v" + i, "/");
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Range = Range(from, from.AddDays(2)), TimeZone = TimeZoneInfo.FindSystemTimeZoneById(zoneId),
      Source = QuerySource.RawEvents, GroupBy = { StandardDimensions.HourOfDay, StandardDimensions.DayOfWeek },
      Metrics = { Metric.Events }
    };
    var raw = engine.Query(query);
    query.Source = QuerySource.Rollups;
    var rollup = engine.Query(query);
    Assert.That(rollup.Rows.ToDictionary(x => string.Join("/", x.Dimensions), x => x[Metric.Events]),
        Is.EquivalentTo(raw.Rows.ToDictionary(x => string.Join("/", x.Dimensions), x => x[Metric.Events])));
    Assert.That(rollup.Plan.IsComplete, Is.True);
    if (zoneId is "Asia/Kolkata" or "Asia/Kathmandu" or "Australia/Lord_Howe")
      Assert.That(rollup.Plan.Steps.All(x => x.Source == PlanSource.MinuteRollups), Is.True);
    foreach (var row in raw.Rows)
      Assert.That(int.Parse(row[StandardDimensions.DayOfWeek], CultureInfo.InvariantCulture), Is.InRange(1, 7));
  }

  [Test]
  public void DailyRollupsComputeWeekdaysInStorageTimezone()
  {
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    using var engine = Create(o => o.TimeZone = zone);
    var start = new DateTime(2025, 11, 2, 4, 0, 0);
    Track(engine, start.AddHours(1), "a", "/");
    Track(engine, start.AddHours(25), "b", "/");
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Range = Range(start, start.AddHours(49)), Source = QuerySource.Rollups,
      GroupBy = { StandardDimensions.DayOfWeek }, Metrics = { Metric.Events }
    };
    var result = engine.Query(query);
    Assert.That(result.Plan.Steps.All(x => x.Source == PlanSource.DayRollups), Is.True);
    Assert.That(result.Rows.ToDictionary(x => x[StandardDimensions.DayOfWeek], x => x[Metric.Events]),
        Is.EquivalentTo(new Dictionary<string, double> { ["7"] = 1, ["1"] = 1 }));
  }

  [Test]
  public void DerivedRollupsCheckAdjustmentRulesOnAdjacentLocalDates()
  {
    var midnight = new DateTime(1, 1, 1);
    var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31),
        TimeSpan.Zero, TimeZoneInfo.TransitionTime.CreateFixedDateRule(midnight, 1, 1),
        TimeZoneInfo.TransitionTime.CreateFixedDateRule(midnight, 12, 31), TimeSpan.FromMinutes(30));
    var zone = TimeZoneInfo.CreateCustomTimeZone("local-year-boundary", TimeSpan.FromHours(12), "local-year-boundary",
        "standard", "daylight", [rule]);
    using var engine = Create();
    var from = new DateTime(2024, 12, 31);
    Track(engine, from.AddHours(12).AddMinutes(5), "a", "/");
    Track(engine, from.AddHours(12).AddMinutes(35), "b", "/");
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Range = Day(from), TimeZone = zone, Source = QuerySource.RawEvents,
      GroupBy = { StandardDimensions.HourOfDay }, Metrics = { Metric.Events }
    };
    var raw = engine.Query(query);
    query.Source = QuerySource.Rollups;
    var rollup = engine.Query(query);
    Assert.That(raw.Rows, Has.Count.EqualTo(2));
    Assert.That(rollup.Rows.ToDictionary(x => x[StandardDimensions.HourOfDay], x => x[Metric.Events]),
        Is.EquivalentTo(raw.Rows.ToDictionary(x => x[StandardDimensions.HourOfDay], x => x[Metric.Events])));
    Assert.That(rollup.Plan.Steps.All(x => x.Source == PlanSource.MinuteRollups), Is.True);
  }

  [Test]
  public void IncompatibleRollupsFallBackToRawAndForcedRollupsFailClearly()
  {
    using var engine = Create(o => o.Rollups.Granularities = Granularity.Day);
    Track(engine, Day0.AddHours(10), "a", "/");
    engine.Flush();
    var query = new AnalyticsQuery { Range = Day(Day0), GroupBy = { StandardDimensions.HourOfDay }, Metrics = { Metric.Events } };
    Assert.That(engine.Query(query).Plan.UsedRawEvents, Is.True);
    query.Source = QuerySource.Rollups;
    Assert.That(Assert.Throws<AnalyticsQueryException>(() => engine.Query(query)).Message, Does.Contain("granularity"));
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void TimeAndOrFiltersSupportSessionAndPageFlowMetrics(QuerySource source)
  {
    using var engine = Create(o => o.Web.Bots = BotTraffic.TrackSeparately);
    Track(engine, Day0.AddHours(9).AddMinutes(55), "a", "/first", "DE");
    Track(engine, Day0.AddHours(10).AddMinutes(5), "a", "/last", "DE");
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Range = Day(Day0), Source = source, GroupBy = { StandardDimensions.HourOfDay },
      Filters = { Filter.AnyOf(Filter.Equal("country", "DE"), Filter.Equal("country", "US")) },
      Metrics = { Metric.Sessions, Metric.Entrances, Metric.Exits, Metric.PageViews }
    };
    var result = engine.Query(query);
    var nine = result.Rows.Single(x => x[StandardDimensions.HourOfDay] == "9");
    var ten = result.Rows.Single(x => x[StandardDimensions.HourOfDay] == "10");
    Assert.That(nine[Metric.Sessions], Is.EqualTo(1));
    Assert.That(nine[Metric.Entrances], Is.EqualTo(1));
    Assert.That(nine[Metric.Exits], Is.Zero);
    Assert.That(ten[Metric.Exits], Is.EqualTo(1));
    var web = engine.Web.GetBreakdown(new WebReportRequest { Range = Day(Day0), Filters = { query.Filters[0] } }, StandardDimensions.HourOfDay);
    Assert.That(web.Rows.Single(x => x.Value == "9").Sessions, Is.EqualTo(1));

    engine.Track(new AnalyticsEvent("pageview")
    {
      Timestamp = Utc(Day0.AddHours(10)), Url = "https://example.com/pricing",
      UserAgent = UserAgents.Googlebot
    });
    engine.Flush();
    var botFilter = Filter.AnyOf(Filter.Equal(StandardDimensions.HourOfDay, "10"), Filter.Equal("page", "/pricing"));
    var bots = engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Source = source, GroupBy = { StandardDimensions.HourOfDay },
      Filters = { botFilter }, Metrics = { Metric.BotHits }
    });
    Assert.That(bots.Rows.Single()[StandardDimensions.HourOfDay], Is.EqualTo("10"));
    Assert.That(bots.Totals[Metric.BotHits], Is.EqualTo(1));
    var summary = engine.Web.GetSummary(new WebReportRequest
    {
      Range = Day(Day0), Filters = { Filter.AnyOf(Filter.Equal(StandardDimensions.HourOfDay, "9"), Filter.Equal(StandardDimensions.HourOfDay, "10")) }
    });
    Assert.That(summary.Summary.BotHits, Is.EqualTo(1));
  }

  [TestCase(StandardDimensions.HourOfDay, Granularity.Hour)]
  [TestCase(StandardDimensions.DayOfWeek, Granularity.Day)]
  public void DerivedDimensionsWorkWithoutRawStorage(string dimension, Granularity granularity)
  {
    using var engine = Create(o =>
    {
      o.Storage.StoreRawEvents = false;
      o.Rollups.Granularities = granularity;
    });
    Track(engine, Day0.AddHours(9), "a", "/");
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery { Range = Day(Day0), GroupBy = { dimension }, Metrics = { Metric.Events } });
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    Assert.That(result.Plan.UsedRawEvents, Is.False);
    Assert.That(result.Plan.IsComplete, Is.True);
  }

  [TestCase(StandardDimensions.HourOfDay)]
  [TestCase(StandardDimensions.DayOfWeek)]
  public void FractionalOffsetsUseRawWhenNoCompatibleFineRollupsExist(string dimension)
  {
    using var engine = Create(o => o.Rollups.Granularities = Granularity.Hour | Granularity.Day);
    Track(engine, Day0.AddHours(18).AddMinutes(25), "a", "/");
    Track(engine, Day0.AddHours(18).AddMinutes(35), "b", "/");
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Range = Day(Day0), TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"),
      GroupBy = { dimension }, Metrics = { Metric.Events }
    };
    var result = engine.Query(query);
    Assert.That(result.Plan.UsedRawEvents, Is.True);
    Assert.That(result.Plan.UsedRollups, Is.False);
    Assert.That(result.Rows, Has.Count.EqualTo(2));
    Assert.That(result.Plan.RollupSuggestions, Is.Empty);
    query.Source = QuerySource.Rollups;
    Assert.That(Assert.Throws<AnalyticsQueryException>(() => engine.Query(query)).Message, Does.Contain(dimension));
  }

  [Test]
  public void RollupSuggestionsAreEmptyForBoundaryReadsAndExistingDefinitions()
  {
    using var engine = Create(o => o.Rollups.Granularities = Granularity.Hour);
    Track(engine, Day0.AddHours(9).AddMinutes(15), "a", "/", "DE");
    engine.Flush();
    var query = new AnalyticsQuery { Range = Day(Day0), GroupBy = { "country" }, Metrics = { Metric.Events } };
    Assert.That(engine.Query(query).Plan.RollupSuggestions, Is.Empty);
    query.Range = Range(Day0.AddHours(9).AddMinutes(10), Day0.AddHours(9).AddMinutes(20));
    var result = engine.Query(query);
    Assert.That(result.Plan.UsedRawEvents, Is.True);
    Assert.That(result.Plan.RollupSuggestions, Is.Empty);
    query.GroupBy.Add("hostname"); // Missing definition cannot help a range shorter than a bucket.
    Assert.That(engine.Query(query).Plan.RollupSuggestions, Is.Empty);
    query.GroupBy.Clear();
    query.GroupBy.Add("missing,dimension");
    query.Range = Day(Day0);
    result = engine.Query(query);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    Assert.That(result.Plan.RollupSuggestions, Is.Empty);
  }

  [Test]
  public void SegmentsAndFunnelsHonorNestedFiltersAndQueryTimezone()
  {
    using var engine = Create();
    Track(engine, Day0.AddHours(9), "a", "/pricing", "DE");
    Track(engine, Day0.AddHours(9).AddMinutes(5), "a", "/pricing", "DE", "signup");
    Track(engine, Day0.AddHours(12), "b", "/pricing", "US");
    Track(engine, Day0.AddHours(12).AddMinutes(5), "b", "/pricing", "US", "signup");
    engine.Flush();
    var zone = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus-two", "plus-two");
    var selection = Filter.AnyOf(Filter.AllOf(Filter.Equal(StandardDimensions.HourOfDay, "11"), Filter.Equal("country", "DE")),
        Filter.Equal("country", "FR"));
    var segment = Segment.VisitorsWho("pageview", selection);
    var query = new AnalyticsQuery { Range = Day(Day0), TimeZone = zone, Segments = { Segment.Parse(segment.ToString()) }, Metrics = { Metric.Events } };
    Assert.That(engine.Query(query).Totals[Metric.Events], Is.EqualTo(1));
    var funnel = new FunnelQuery
    {
      Range = Day(Day0), TimeZone = zone,
      Filters = { Filter.AnyOf(Filter.Equal("country", "DE"), Filter.Equal("country", "US")) },
      Steps = { new FunnelStep("visit", "pageview", selection), FunnelStep.Goal("signup", Filter.Equal(StandardDimensions.HourOfDay, "11")) }
    };
    Assert.That(engine.Query(funnel).Steps[1].Visitors, Is.EqualTo(1));
    funnel.Segments.Add(segment);
    Assert.That(engine.Query(funnel).Steps[1].Visitors, Is.EqualTo(1));
  }

  [Test]
  public void SuggestionsNameExactStoredDimensionsAndCanBeAppliedToNewData()
  {
    using var engine = Create();
    Track(engine, Day0.AddHours(9), "a", "/", "DE");
    engine.Flush();
    var selected = Metric.Events.Where(Filter.Equal(StandardDimensions.HourOfDay, "9"));
    var query = new AnalyticsQuery
    {
      Range = Day(Day0), GroupBy = { "page", "hostname", StandardDimensions.HourOfDay },
      Filters = { Selection() }, Metrics = { Metric.Events, selected }
    };
    var plan = engine.Query(query).Plan;
    Assert.That(plan.UsedRawEvents, Is.True);
    Assert.That(plan.IsComplete, Is.True);
    Assert.That(plan.RollupSuggestions, Has.Count.EqualTo(1));
    var suggestion = plan.RollupSuggestions.Single();
    Assert.That(suggestion.Event, Is.EqualTo("pageview"));
    Assert.That(suggestion.Definition.Dimensions, Is.EqualTo(new[] { "country", "hostname", "page" }));
    using var configured = TestEngine.Create(Path.Combine(directory, "suggested"), o => o.Rollups.Definitions.Add(suggestion.Definition));
    Track(configured, Day0.AddHours(9), "a", "/", "DE");
    configured.Flush();
    var result = configured.Query(query);
    Assert.That(result.Plan.UsedRawEvents, Is.False);
    Assert.That(result.Plan.RollupSuggestions, Is.Empty);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    Assert.That(result.Totals[selected], Is.EqualTo(1));
  }

  [TestCase("forced")]
  [TestCase("aggregate")]
  [TestCase("segment")]
  [TestCase("disabled")]
  public void SuggestionsDoNotMisdiagnoseOtherRawFallbackReasons(string reason)
  {
    using var engine = Create(o =>
    {
      if (reason == "disabled")
        o.Rollups.Granularities = Granularity.None;
    });
    Track(engine, Day0.AddHours(9), "a", "/", "DE");
    engine.Flush();
    var query = new AnalyticsQuery { Range = Day(Day0), GroupBy = { "page", "hostname" }, Metrics = { Metric.Events } };
    if (reason == "forced")
      query.Source = QuerySource.RawEvents;
    if (reason == "aggregate")
      query.Metrics.Add(Metric.Percentile("value", 95));
    if (reason == "segment")
      query.Segments.Add(Segment.VisitorsWhoViewed("/"));
    Assert.That(engine.Query(query).Plan.RollupSuggestions, Is.Empty);
  }
}
