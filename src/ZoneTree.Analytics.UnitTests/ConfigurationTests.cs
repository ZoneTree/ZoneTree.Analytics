namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// How configuration behaves across the lifetime of an engine and of a data directory.
/// </summary>
public sealed class ConfigurationTests
{
  static readonly DateTime T0 = DateTime.UtcNow.Date.AddDays(-2).AddHours(10);

  string directory;

  [SetUp]
  public void SetUp() => directory = TestEngine.NewDirectory("configuration");

  [TearDown]
  public void TearDown() => TestEngine.Delete(directory);

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  static DateRange Range => DateRange.Between(Utc(T0.Date), Utc(T0.Date.AddDays(1)));

  static AnalyticsEvent Event(string name, string plan, int minute) =>
      new AnalyticsEvent(name) { VisitorId = "v" + minute, Timestamp = Utc(T0.AddMinutes(minute)) }
          .WithDimension("plan", plan);

  static AnalyticsResult ByPlan(AnalyticsEngine engine, string eventName) => engine.Query(new AnalyticsQuery
  {
    Event = eventName,
    Range = Range,
    GroupBy = { "plan" },
    Metrics = { Metric.Events },
  });

  [Test]
  public void ReAddedRollupDoesNotClaimTheTimeItWasMissing()
  {
    using (var engine = TestEngine.Create(directory, o => o.Rollups.Definitions.Add(new RollupDefinition("plan"))))
      engine.Track(Event("purchase", "pro", 0));
    // Without the rollup, this purchase only reaches raw events.
    using (var engine = TestEngine.Create(directory))
      engine.Track(Event("purchase", "pro", 60));
    using (var engine = TestEngine.Create(directory, o => o.Rollups.Definitions.Add(new RollupDefinition("plan"))))
    {
      var result = ByPlan(engine, "purchase");
      Assert.Multiple(() =>
      {
        Assert.That(result.Rows.Single()[Metric.Events], Is.EqualTo(2));
        Assert.That(result.Plan.UsedRawEvents, Is.True);
      });
    }
  }

  [Test]
  public void ChangingTheEventsOfARollupStartsANewActivation()
  {
    using (var engine = TestEngine.Create(directory, o =>
        o.Rollups.Definitions.Add(new RollupDefinition("plan") { Events = ["purchase"] })))
    {
      engine.Track(Event("signup", "free", 0));
    }
    using (var engine = TestEngine.Create(directory, o => o.Rollups.Definitions.Add(new RollupDefinition("plan"))))
    {
      var result = ByPlan(engine, "signup");
      Assert.Multiple(() =>
      {
        // The restricted rollup never held signups; they come from raw events.
        Assert.That(result.Rows.Single()[Metric.Events], Is.EqualTo(1));
        Assert.That(result.Plan.UsedRawEvents, Is.True);
      });
    }
  }

  [Test]
  public void UnchangedRollupsKeepAnsweringFromRollups()
  {
    using (var engine = TestEngine.Create(directory, o => o.Rollups.Definitions.Add(new RollupDefinition("plan"))))
      engine.Track(Event("purchase", "pro", 0));
    using (var engine = TestEngine.Create(directory, o => o.Rollups.Definitions.Add(new RollupDefinition("plan"))))
    {
      var result = ByPlan(engine, "purchase");
      Assert.Multiple(() =>
      {
        Assert.That(result.Rows.Single()[Metric.Events], Is.EqualTo(1));
        Assert.That(result.Plan.UsedRawEvents, Is.False);
      });
    }
  }

  [Test]
  public void OptionsAreFixedWhenTheEngineIsCreated()
  {
    var options = TestEngine.Options(directory, o => o.Rollups.Definitions.Add(new RollupDefinition("plan")));
    using var engine = new AnalyticsEngine(options);

    // None of these reach the running engine.
    options.Privacy.DeniedDimensions = new HashSet<string> { "plan" };
    options.Privacy.MaxCustomDimensions = 0;
    options.EventFilters.Add(EventFilter.Create(_ => false));
    options.Rollups.Definitions.Clear();
    options.Queries.Timeout = TimeSpan.FromTicks(1);
    options.Sessions.Dimensions.Add("plan");

    Assert.That(engine.Track(Event("purchase", "pro", 0)), Is.True);
    engine.Flush();
    var result = ByPlan(engine, "purchase");
    Assert.Multiple(() =>
    {
      Assert.That(result.Rows.Single()["plan"], Is.EqualTo("pro"));
      Assert.That(result.Plan.UsedRawEvents, Is.False);
    });
  }
}
