using System.Globalization;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Custom session-level dimensions.
/// </summary>
/// <remarks>
/// Visitor 1 is in variant A: views / then /pricing; the second event says B, which is ignored.
/// Visitor 2 is in variant B: views / and bounces. Visitor 3 has no variant: views / and bounces.
/// </remarks>
public sealed class SessionDimensionTests
{
  static readonly DateTime T0 = DateTime.UtcNow.Date.AddDays(-2).AddHours(10);

  static readonly QuerySource[] Sources = [QuerySource.Rollups, QuerySource.RawEvents];

  const string Variant = "ab_variant";

  string directory;

  AnalyticsEngine engine;

  [SetUp]
  public void SetUp()
  {
    directory = TestEngine.NewDirectory("session-dimensions");
    engine = Create();
  }

  [TearDown]
  public void TearDown()
  {
    engine?.Dispose();
    TestEngine.Delete(directory);
  }

  AnalyticsEngine Create(Action<AnalyticsOptions> configure = null) =>
      TestEngine.Create(directory, o =>
      {
        o.Sessions.Dimensions.Add(Variant);
        o.Rollups.Definitions.Add(new RollupDefinition(Variant));
        configure?.Invoke(o);
      });

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  static DateRange Range => DateRange.Between(Utc(T0.Date), Utc(T0.Date.AddDays(1)));

  static AnalyticsEvent PageView(string visitor, string page, int minute, string variant)
  {
    var e = new AnalyticsEvent(StandardEvents.PageView)
    {
      VisitorId = visitor,
      Url = "https://mysite.com" + page,
      Timestamp = Utc(T0.AddMinutes(minute))
    };
    return variant == null ? e : e.WithDimension(Variant, variant);
  }

  void TrackScenario()
  {
    engine.Track(PageView("1", "/", 0, "A"));
    engine.Track(PageView("1", "/pricing", 1, "B"));
    engine.Track(PageView("2", "/", 2, "B"));
    engine.Track(PageView("3", "/", 3, null));
    engine.Flush();
  }

  Dictionary<string, double[]> ByVariant(QuerySource source, params Metric[] metrics)
  {
    var query = new AnalyticsQuery
    {
      Range = Range,
      Source = source,
      GroupBy = { Variant },
    };
    query.Metrics.AddRange(metrics);
    var result = engine.Query(query);
    return result.Rows.ToDictionary(x => x[Variant] ?? "(none)", x => metrics.Select(m => x[m]).ToArray());
  }

  [TestCaseSource(nameof(Sources))]
  public void SessionMetricsGroupByCustomSessionDimensions(QuerySource source)
  {
    TrackScenario();
    var rows = ByVariant(source, Metric.Sessions, Metric.BounceRate, Metric.PageViews, Metric.Exits);
    Assert.That(rows, Is.EquivalentTo(new Dictionary<string, double[]>
    {
      ["A"] = [1, 0, 2, 1],
      ["B"] = [1, 1, 1, 1],
      ["(none)"] = [1, 1, 1, 1],
    }));
  }

  [Test]
  public async Task WebBreakdownReportsSessionMetricsForCustomSessionDimensions()
  {
    TrackScenario();
    var breakdown = await engine.Web.GetBreakdownAsync(new WebReportRequest { Range = Range }, Variant);
    var filtered = new WebReportRequest { Range = Range };
    filtered.Filters.Add(Filter.Equal(Variant, "A"));
    var summary = await engine.Web.GetSummaryAsync(filtered);
    var pages = await engine.Web.GetExitPagesAsync(filtered);
    var a = breakdown.Rows.Single(x => x.Value == "A");
    Assert.Multiple(() =>
    {
      Assert.That(a.Sessions, Is.EqualTo(1));
      Assert.That(a.BounceRate, Is.EqualTo(0));
      Assert.That(a.AverageSessionDuration, Is.EqualTo(TimeSpan.FromMinutes(1)));
      Assert.That(summary.Sessions, Is.EqualTo(1));
      Assert.That(summary.PageViews, Is.EqualTo(2));
      Assert.That(pages.Rows.Single().Page, Is.EqualTo("/pricing"));
      Assert.That(breakdown.Plan.UsedRawEvents, Is.False);
    });
  }

  [Test]
  public void EnrichersCanSetCustomSessionDimensions()
  {
    engine.Dispose();
    engine = Create(o => o.Enrichers.Add(EventEnricher.Create(context =>
    {
      if (context.GetDimension(Variant) == null)
        context.SetDimension(Variant, context.GetDimension(StandardDimensions.Page) == "/" ? "home" : "deep");
    })));
    engine.Track(PageView("1", "/", 0, null));
    engine.Track(PageView("1", "/pricing", 1, null));
    engine.Track(PageView("2", "/pricing", 2, null));
    engine.Flush();
    Assert.That(ByVariant(QuerySource.Rollups, Metric.Sessions, Metric.PageViews), Is.EquivalentTo(new Dictionary<string, double[]>
    {
      ["home"] = [1, 2],
      ["deep"] = [1, 1],
    }));
  }

  [Test]
  public void SessionsKeepCustomDimensionsAcrossRestartsAndSchemaChanges()
  {
    engine.Track(PageView("1", "/", 0, "A"));
    engine.Flush();
    engine.Dispose();
    // A new custom dimension listed first: stored values are matched by name, not position.
    engine = Create(o => o.Sessions.Dimensions.Insert(0, "plan"));
    engine.Track(PageView("1", "/pricing", 5, "B"));
    engine.Flush();
    Assert.Multiple(() =>
    {
      Assert.That(ByVariant(QuerySource.RawEvents, Metric.PageViews, Metric.Sessions), Is.EquivalentTo(new Dictionary<string, double[]>
      {
        ["A"] = [2, 1],
      }));
      Assert.That(engine.GetStatistics().SessionsStarted, Is.EqualTo(0));
    });
  }

  [Test]
  public void WithoutSessionsCustomSessionDimensionsArePerEvent()
  {
    engine.Dispose();
    engine = Create(o => o.Sessions.Enabled = false);
    TrackScenario();
    Assert.That(ByVariant(QuerySource.Rollups, Metric.PageViews), Is.EquivalentTo(new Dictionary<string, double[]>
    {
      ["A"] = [1],
      ["B"] = [2],
      ["(none)"] = [1],
    }));
  }

  [TestCase("country", "standard")]
  [TestCase("page", "maintained by the engine")]
  [TestCase("bot", "maintained by the engine")]
  [TestCase("$x", "not a valid")]
  [TestCase(" padded", "not a valid")]
  [TestCase("a,b", "not a valid")]
  [TestCase(Variant, "Duplicate")]
  public void InvalidSessionDimensionsAreRejected(string name, string message)
  {
    engine.Dispose();
    engine = null;
    TestEngine.Delete(directory);
    var exception = Assert.Throws<ArgumentException>(() => Create(o => o.Sessions.Dimensions.Add(name)).Dispose());
    Assert.That(exception.Message, Does.Contain(message));
  }

  [Test]
  public void SessionDimensionsMustBeAllowedByPrivacyOptions()
  {
    engine.Dispose();
    engine = null;
    Assert.Throws<ArgumentException>(() => Create(o => o.Privacy.DeniedDimensions.Add(Variant)).Dispose());
    Assert.Throws<ArgumentException>(() => Create(o => o.Privacy.AllowedDimensions = new HashSet<string> { "plan" }).Dispose());
  }
}

/// <summary>
/// Query limits, timeouts, metric limits and the maintainer hook.
/// </summary>
public sealed class LimitsTests
{
  static readonly DateTime T0 = DateTime.UtcNow.Date.AddDays(-2).AddHours(10);

  string directory;

  [SetUp]
  public void SetUp() => directory = TestEngine.NewDirectory("limits");

  [TearDown]
  public void TearDown() => TestEngine.Delete(directory);

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  static DateRange Range => DateRange.Between(Utc(T0.Date), Utc(T0.Date.AddDays(1)));

  [Test]
  public void TimeSeriesWithTooManyBucketsAreRejected()
  {
    using var engine = TestEngine.Create(directory);
    var range = DateRange.Between(Utc(T0.AddDays(-100)), Utc(T0));
    var exception = Assert.Throws<AnalyticsQueryException>(() =>
        engine.Query(new AnalyticsQuery { Range = range, Interval = TimeInterval.Minute }));
    Assert.Multiple(() =>
    {
      Assert.That(exception.Message, Does.Contain("100,000 minute buckets"));
      Assert.That(engine.Query(new AnalyticsQuery { Range = range, Interval = TimeInterval.Hour }).Rows, Has.Count.EqualTo(2400));
    });
  }

  [Test]
  public async Task SlowQueriesTimeOut()
  {
    var query = new AnalyticsQuery { Event = "e", Range = Range, Source = QuerySource.RawEvents };
    using (var engine = TestEngine.Create(directory, o => o.Queries.Timeout = TimeSpan.FromTicks(1)))
    {
      for (var i = 0; i < 3000; ++i)
        engine.Track(new AnalyticsEvent("e") { VisitorId = i.ToString(CultureInfo.InvariantCulture), Timestamp = Utc(T0.AddSeconds(i)) });
      engine.Flush();
      var funnel = new FunnelQuery { Range = Range, Steps = { FunnelStep.Goal("e"), FunnelStep.Goal("f") } };
      var timeout = Assert.Throws<AnalyticsQueryTimeoutException>(() => engine.Query(query));
      Assert.ThrowsAsync<AnalyticsQueryTimeoutException>(() => engine.QueryAsync(query));
      Assert.ThrowsAsync<AnalyticsQueryTimeoutException>(() => engine.QueryAsync(funnel));
      Assert.That(timeout.Timeout, Is.EqualTo(TimeSpan.FromTicks(1)));
    }

    // The caller's own cancellation is not a timeout.
    using var unlimited = TestEngine.Create(directory, o => o.Queries.Timeout = null);
    using var cancelled = new CancellationTokenSource();
    await cancelled.CancelAsync();
    Assert.Catch<OperationCanceledException>(() => unlimited.Query(query, cancelled.Token));
    Assert.That(unlimited.Query(query).Totals[Metric.Events], Is.EqualTo(3000));
  }

  [Test]
  public void CustomMetricsPerEventAreLimited()
  {
    using var engine = TestEngine.Create(directory, o => o.Privacy.MaxCustomMetrics = 2);
    engine.Track(new AnalyticsEvent("e") { Timestamp = Utc(T0) }
        .WithMetric("a", 1).WithMetric("b", 2).WithMetric("c", 3));
    engine.Flush();
    var totals = engine.Query(new AnalyticsQuery
    {
      Event = "e",
      Range = Range,
      Metrics = { Metric.Count("a"), Metric.Count("b"), Metric.Count("c") },
    }).Totals;
    Assert.That(new[] { totals[Metric.Count("a")], totals[Metric.Count("b")], totals[Metric.Count("c")] },
        Is.EqualTo(new[] { 1.0, 1.0, 0.0 }));
  }

  [Test]
  public void MaintainerHooksConfigureEachStore()
  {
    var configured = new System.Collections.Concurrent.ConcurrentBag<string>();
    Action<IMaintainer> Hook(string store) => maintainer =>
    {
      maintainer.ThresholdForMergeOperationStart = 1234;
      configured.Add(store);
    };
    using (var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.RawEvents.ConfigureMaintainer = Hook("raw");
      o.Storage.Rollups.ConfigureMaintainer = Hook("rollups");
      o.Storage.Sessions.ConfigureMaintainer = Hook("sessions");
    }))
    {
      engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Url = "/", VisitorId = "1", Timestamp = Utc(T0) });
      engine.Flush();
    }
    Assert.That(configured.Distinct(), Is.EquivalentTo(new[] { "raw", "rollups", "sessions" }));
  }

  [Test]
  public void InvalidLimitsAreRejected()
  {
    Assert.Throws<ArgumentException>(() => TestEngine.Create(directory, o => o.Queries.Timeout = TimeSpan.Zero).Dispose());
    Assert.Throws<ArgumentException>(() => TestEngine.Create(directory, o => o.Privacy.MaxCustomMetrics = -1).Dispose());
  }
}
