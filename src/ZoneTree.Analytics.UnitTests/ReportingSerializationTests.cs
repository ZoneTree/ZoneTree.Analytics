using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ZoneTree.Analytics.AspNetCore;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class ReportingSerializationTests
{
  static readonly DateTimeOffset From = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

  static readonly DateTimeOffset To = From.AddDays(1);

  static QueryPlan Plan() => new([], []);

  static WebReport<T> Report<T>(params T[] rows) => new(rows, rows.Length, From, To, Plan());

  static JsonElement Serialize<T>(T response, JsonTypeInfo<T> typeInfo) =>
      JsonSerializer.SerializeToElement(response, typeInfo);

  static void AssertJson(JsonElement actual, string expected)
  {
    Assert.That(JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()), JsonNode.Parse(expected)), Is.True,
        () => "Actual JSON: " + actual.GetRawText());
  }

  [Test]
  public void SummaryContractKeepsNullMetricsOmitsMissingComparisonAndUsesSeconds()
  {
    var summary = new WebSummary(2, 3, null, null, TimeSpan.FromMilliseconds(1250), 1.5, null);
    var response = new ReportingResponseMapper().Summary(new(summary, From, To, Plan()));
    AssertJson(Serialize(response, ReportingJsonContext.Default.SummaryResponse), """
      {
        "from": "2025-01-01T00:00:00+00:00", "to": "2025-01-02T00:00:00+00:00",
        "summary": {
          "visitors": 2, "pageViews": 3, "sessions": null, "bounceRate": null,
          "averageSessionDuration": 1.25, "pageViewsPerSession": 1.5, "botHits": null
        },
        "plan": { "complete": true, "warnings": [], "steps": [], "rollupSuggestions": [] }
      }
      """);
  }

  [TestCase(9007199254740993L)]
  [TestCase(long.MaxValue)]
  public void SummaryAndBreakdownCountsRetainInt64PrecisionIncludingComparisons(long count)
  {
    var summary = new WebSummary(count, count, count, 0, null, 1, count);
    summary = summary with { Previous = summary };
    var summaryJson = Serialize(new ReportingResponseMapper().Summary(new(summary, From, To, Plan())),
        ReportingJsonContext.Default.SummaryResponse).GetProperty("summary");
    var row = new WebBreakdownRow("Direct", count, count, 1, count, 0, null);
    row = row with { Previous = row };
    var breakdownJson = Serialize(new ReportingResponseMapper().Breakdown(Report(row)),
        ReportingJsonContext.Default.BreakdownResponse).GetProperty("rows")[0];
    Assert.Multiple(() =>
    {
      foreach (var json in new[] { summaryJson, summaryJson.GetProperty("previous") })
        foreach (var name in new[] { "visitors", "pageViews", "sessions", "botHits" })
          Assert.That(json.GetProperty(name).GetInt64(), Is.EqualTo(count), name);
      foreach (var json in new[] { breakdownJson, breakdownJson.GetProperty("previous") })
        foreach (var name in new[] { "visitors", "pageViews", "sessions" })
          Assert.That(json.GetProperty(name).GetInt64(), Is.EqualTo(count), name);
    });
  }

  [TestCase(9007199254740993L)]
  [TestCase(long.MaxValue)]
  public void PageAndFunnelCountsRetainInt64PrecisionIncludingComparisons(long count)
  {
    var page = new PageStats("/", count, count, count, count, 1, count);
    page = page with { Previous = page };
    var pageJson = Serialize(new ReportingResponseMapper().Pages(Report(page)),
        ReportingJsonContext.Default.PagesResponse).GetProperty("rows")[0];
    var funnel = new FunnelResult([new("Signup", count, 1, 1, count)], From, To, Plan());
    var funnelJson = Serialize(new ReportingResponseMapper().Funnel(funnel),
        ReportingJsonContext.Default.FunnelResponse).GetProperty("steps")[0];
    Assert.Multiple(() =>
    {
      foreach (var json in new[] { pageJson, pageJson.GetProperty("previous") })
        foreach (var name in new[] { "visitors", "pageViews", "entrances", "exits", "botHits" })
          Assert.That(json.GetProperty(name).GetInt64(), Is.EqualTo(count), name);
      Assert.That(funnelJson.GetProperty("visitors").GetInt64(), Is.EqualTo(count));
      Assert.That(funnelJson.GetProperty("dropOff").GetInt64(), Is.EqualTo(count));
    });
  }

  [Test]
  public void ListRowContractsKeepNamesNullsAndOptionalComparisons()
  {
    var json = ReportingJsonContext.Default;
    AssertJson(Serialize(new ReportingResponseMapper().Breakdown(Report(
        new WebBreakdownRow(null, 2, 3, 0.5, null, null, TimeSpan.FromSeconds(2.5)))), json.BreakdownResponse)
        .GetProperty("rows")[0], """
      { "value": null, "visitors": 2, "pageViews": 3, "visitorShare": 0.5,
        "sessions": null, "bounceRate": null, "averageSessionDuration": 2.5 }
      """);
    AssertJson(Serialize(new ReportingResponseMapper().Pages(Report(
        new PageStats("/", 2, 3, null, null, null, null))), json.PagesResponse).GetProperty("rows")[0], """
      { "page": "/", "visitors": 2, "pageViews": 3, "entrances": null,
        "exits": null, "exitRate": null, "botHits": null }
      """);
    AssertJson(Serialize(new ReportingResponseMapper().Goals(Report(
        new GoalStats("Signup", 2, 3, 0.5))), json.GoalsResponse).GetProperty("rows")[0], """
      { "name": "Signup", "visitors": 2, "events": 3, "conversionRate": 0.5 }
      """);
    var bot = new BotStats("Bot", "Search", 3, 0.5) { Previous = new("Bot", "Search", 1, 0.25) };
    AssertJson(Serialize(new ReportingResponseMapper().Bots(Report(bot)), json.BotsResponse).GetProperty("rows")[0], """
      { "name": "Bot", "category": "Search", "hits": 3, "share": 0.5,
        "previous": { "name": "Bot", "category": "Search", "hits": 1, "share": 0.25 } }
      """);
  }

  [Test]
  public void EmptyListContractKeepsTotalRowsAndEmptyArrays()
  {
    var report = new WebReport<PageStats>([], 42, From, To, Plan());
    AssertJson(Serialize(new ReportingResponseMapper().Pages(report), ReportingJsonContext.Default.PagesResponse), """
      { "from": "2025-01-01T00:00:00+00:00", "to": "2025-01-02T00:00:00+00:00",
        "totalRows": 42, "rows": [],
        "plan": { "complete": true, "warnings": [], "steps": [], "rollupSuggestions": [] } }
      """);
  }

  [TestCase(double.NaN)]
  [TestCase(double.PositiveInfinity)]
  [TestCase(double.NegativeInfinity)]
  public void NonFiniteComparisonsBecomeNullAndWarnWithoutMutatingEngineResults(double value)
  {
    var plan = new QueryPlan([], ["Existing warning"]);
    var previous = new WebSummary(1, 1, 1, value, null, value, null);
    var summary = new WebSummary(2, 3, 2, 0.5, null, 1.5, null) { Previous = previous };
    var response = new ReportingResponseMapper().Summary(new(summary, From, To, plan));
    var json = Serialize(response, ReportingJsonContext.Default.SummaryResponse);
    var comparison = json.GetProperty("summary").GetProperty("previous");
    Assert.Multiple(() =>
    {
      Assert.That(comparison.GetProperty("bounceRate").ValueKind, Is.EqualTo(JsonValueKind.Null));
      Assert.That(comparison.GetProperty("pageViewsPerSession").ValueKind, Is.EqualTo(JsonValueKind.Null));
      Assert.That(json.GetProperty("plan").GetProperty("complete").GetBoolean(), Is.False);
      var warnings = json.GetProperty("plan").GetProperty("warnings").EnumerateArray().Select(x => x.GetString()).ToArray();
      Assert.That(warnings, Has.Length.EqualTo(2));
      Assert.That(warnings[0], Is.EqualTo("Existing warning"));
      Assert.That(warnings[1], Does.Contain("Non-finite metric values"));
      Assert.That(plan.Warnings, Is.EqualTo(new[] { "Existing warning" }));
      Assert.That(summary.Previous.BounceRate, Is.EqualTo(value));
    });
  }

  [Test]
  public void OverflowWarningsCoverEveryReportTypeAndComparison()
  {
    var json = ReportingJsonContext.Default;
    var summary = new WebSummary(1, 1, 1, double.NaN, null, 1, null);
    var breakdown = new WebBreakdownRow("Direct", 1, 1, 1, 1, 0, null)
    {
      Previous = new("Direct", 1, 1, double.PositiveInfinity, 1, 0, null)
    };
    var page = new PageStats("/", 1, 1, 1, 1, 1, null)
    {
      Previous = new("/", 1, 1, 1, 1, double.NegativeInfinity, null)
    };
    var bot = new BotStats("Bot", "Search", 1, 1) { Previous = new("Bot", "Search", 1, double.NaN) };
    var funnel = new FunnelResult([new("Signup", 1, 1, double.NaN, 0)], From, To, Plan());
    var responses = new[]
    {
      Serialize(new ReportingResponseMapper().Timeseries(Report(new WebTimeseriesPoint(From, summary))), json.TimeseriesResponse),
      Serialize(new ReportingResponseMapper().Breakdown(Report(breakdown)), json.BreakdownResponse),
      Serialize(new ReportingResponseMapper().Pages(Report(page)), json.PagesResponse),
      Serialize(new ReportingResponseMapper().Goals(Report(new GoalStats("Signup", 1, 1, double.PositiveInfinity))), json.GoalsResponse),
      Serialize(new ReportingResponseMapper().Bots(Report(bot)), json.BotsResponse),
      Serialize(new ReportingResponseMapper().Funnel(funnel), json.FunnelResponse)
    };
    Assert.Multiple(() =>
    {
      foreach (var response in responses)
      {
        var plan = response.GetProperty("plan");
        Assert.That(plan.GetProperty("complete").GetBoolean(), Is.False);
        Assert.That(plan.GetProperty("warnings").GetArrayLength(), Is.EqualTo(1));
      }
      Assert.That(responses[0].GetProperty("rows")[0].GetProperty("bucket").GetDateTimeOffset(), Is.EqualTo(From));
      Assert.That(responses[^1].GetProperty("steps")[0].GetProperty("stepConversionRate").ValueKind, Is.EqualTo(JsonValueKind.Null));
    });
  }

  [Test]
  public void QueryContractKeepsDynamicKeysBucketsComparisonsAndNullDimensions()
  {
    const string dimension = "Custom\"Dimension\\Key";
    const string value = "Grüße\n<script>\"\\";
    var metric = Metric.Sum("Revenue");
    var shape = new AnalyticsResultShape([metric], [dimension]);
    var row = new AnalyticsRow(shape, From, [value], [2.5])
    {
      Previous = new(shape, From.AddDays(-1), [null], [double.PositiveInfinity])
    };
    var totals = new AnalyticsRow(shape, null, [null], [2.5]);
    var result = new AnalyticsResult(shape, TimeInterval.Day, From, To, TimeZoneInfo.Utc, [row], 1, totals, Plan());
    var json = Serialize(new ReportingResponseMapper().Query(result), ReportingJsonContext.Default.QueryResponse);
    Assert.Multiple(() =>
    {
      Assert.That(json.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[]
      {
        "from", "to", "timeZone", "interval", "groupBy", "metrics", "totalRows", "totals", "rows", "plan"
      }));
      Assert.That(json.GetProperty("timeZone").GetString(), Is.EqualTo("UTC"));
      Assert.That(json.GetProperty("interval").GetString(), Is.EqualTo("day"));
      Assert.That(json.GetProperty("groupBy")[0].GetString(), Is.EqualTo(dimension));
      Assert.That(json.GetProperty("metrics")[0].GetString(), Is.EqualTo("sum:Revenue"));
      var actualRow = json.GetProperty("rows")[0];
      Assert.That(actualRow.GetProperty("bucket").GetDateTimeOffset(), Is.EqualTo(From));
      Assert.That(actualRow.GetProperty("dimensions").GetProperty(dimension).GetString(), Is.EqualTo(value));
      Assert.That(actualRow.GetProperty("values").GetProperty("sum:Revenue").GetDouble(), Is.EqualTo(2.5));
      Assert.That(actualRow.GetProperty("previous").GetProperty("dimensions").GetProperty(dimension).ValueKind, Is.EqualTo(JsonValueKind.Null));
      Assert.That(actualRow.GetProperty("previous").GetProperty("values").GetProperty("sum:Revenue").ValueKind, Is.EqualTo(JsonValueKind.Null));
      Assert.That(json.GetProperty("totals").TryGetProperty("bucket", out _), Is.False);
      Assert.That(json.GetProperty("totals").TryGetProperty("previous", out _), Is.False);
      Assert.That(json.GetProperty("plan").GetProperty("complete").GetBoolean(), Is.False);
      Assert.That(row.Previous.Values[0], Is.EqualTo(double.PositiveInfinity));
      Assert.That(result.Plan.IsComplete, Is.True);
    });
  }

  [Test]
  public void UngroupedQueryOmitsDimensionsAndBucket()
  {
    var shape = new AnalyticsResultShape([Metric.Events], []);
    var row = new AnalyticsRow(shape, null, [], [1]);
    var result = new AnalyticsResult(shape, TimeInterval.None, From, To, TimeZoneInfo.Utc, [row], 1, row, Plan());
    var json = Serialize(new ReportingResponseMapper().Query(result), ReportingJsonContext.Default.QueryResponse);
    AssertJson(json.GetProperty("rows")[0], """{ "values": { "events": 1 } }""");
    AssertJson(json.GetProperty("totals"), """{ "values": { "events": 1 } }""");
  }

  [Test]
  public void PlanContractProjectsSourcesAndRollupSuggestions()
  {
    var definition = new RollupDefinition("hostname", "page") { Events = ["pageview"] };
    var plan = new QueryPlan([new("pageview", PlanSource.RawEvents, From, To)], [], [new("pageview", definition)]);
    var report = new WebReport<PageStats>([], 0, From, To, plan);
    var json = Serialize(new ReportingResponseMapper().Pages(report), ReportingJsonContext.Default.PagesResponse).GetProperty("plan");
    AssertJson(json.GetProperty("steps")[0], """
      { "event": "pageview", "source": "RawEvents", "from": "2025-01-01T00:00:00+00:00", "to": "2025-01-02T00:00:00+00:00" }
      """);
    var suggestion = json.GetProperty("rollupSuggestions")[0];
    Assert.Multiple(() =>
    {
      Assert.That(suggestion.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "event", "id", "dimensions", "events" }));
      Assert.That(suggestion.GetProperty("event").GetString(), Is.EqualTo("pageview"));
      Assert.That(suggestion.GetProperty("id").GetString(), Is.EqualTo(definition.Id));
      Assert.That(suggestion.GetProperty("dimensions").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(new[] { "hostname", "page" }));
      Assert.That(suggestion.GetProperty("events")[0].GetString(), Is.EqualTo("pageview"));
    });
  }

  [Test]
  public void CoveringIndexPlansExposeTheirDimensionWithoutChangingOtherPlanShapes()
  {
    var plan = new QueryPlan([new("pageview", PlanSource.DimensionIndex, From, To) { IndexDimension = "source" }], []);
    var report = new WebReport<PageStats>([], 0, From, To, plan);
    var json = Serialize(new ReportingResponseMapper().Pages(report), ReportingJsonContext.Default.PagesResponse).GetProperty("plan");
    Assert.That(json.GetProperty("steps")[0].GetProperty("source").GetString(), Is.EqualTo("DimensionIndex"));
    Assert.That(json.GetProperty("steps")[0].GetProperty("indexDimension").GetString(), Is.EqualTo("source"));
    Assert.That(json.GetProperty("complete").GetBoolean(), Is.True);
  }

  [Test]
  public void ErrorAndRealtimeContractsUseGeneratedMetadata()
  {
    const string message = "Invalid \"value\"\nGrüße <script>\\";
    var error = Serialize(new ErrorResponse(message), ReportingJsonContext.Default.ErrorResponse);
    Assert.That(error.GetProperty("error").GetString(), Is.EqualTo(message));
    AssertJson(Serialize(new RealtimeResponse(null, 5), ReportingJsonContext.Default.RealtimeResponse),
        """{ "visitors": null, "minutes": 5 }""");
    var realtime = Serialize(new RealtimeResponse(long.MaxValue, 5), ReportingJsonContext.Default.RealtimeResponse);
    Assert.That(realtime.GetProperty("visitors").GetInt64(), Is.EqualTo(long.MaxValue));
  }
}
