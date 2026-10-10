namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Opening an engine, its properties, and tracking under queue pressure.
/// </summary>
public sealed class EngineEntryPointTests
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-10);

  string directory;

  [SetUp]
  public void SetUp() => directory = TestEngine.NewDirectory("entry");

  [TearDown]
  public void TearDown() => TestEngine.Delete(directory);

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  static DateRange Day(DateTime day) => DateRange.Between(Utc(day), Utc(day.AddDays(1)));

  static double Events(AnalyticsEngine engine, string eventName, DateRange range, string site = null) =>
      engine.Query(new AnalyticsQuery { Site = site, Event = eventName, Range = range, Metrics = { Metric.Events } })
          .Totals[Metric.Events];

  [Test]
  public void OpenUsesTheDirectoryAndTheConfiguration()
  {
    var zone = TimeZoneInfo.CreateCustomTimeZone("plus-three", TimeSpan.FromHours(3), "plus-three", "plus-three");
    using (var engine = AnalyticsEngine.Open(directory, o =>
    {
      TestEngine.UseTestDefaults(o);
      o.DefaultSite = "shop";
      o.TimeZone = zone;
    }))
    {
      engine.Track(new AnalyticsEvent("purchase") { VisitorId = "1", Timestamp = Utc(Day0.AddHours(12)) });
      engine.Flush();
      Assert.Multiple(() =>
      {
        Assert.That(engine.DefaultSite, Is.EqualTo("shop"));
        Assert.That(engine.TimeZone, Is.EqualTo(zone));
        // Events and queries without a site use the default site.
        Assert.That(Events(engine, "purchase", Day(Day0)), Is.EqualTo(1));
        Assert.That(Events(engine, "purchase", Day(Day0), "shop"), Is.EqualTo(1));
        Assert.That(Events(engine, "purchase", Day(Day0), "default"), Is.Zero);
      });
    }
    Assert.That(Directory.Exists(Path.Combine(directory, "meta")), Is.True);

    // Without a configuration, Open uses the defaults; the reporting time zone is persisted.
    var reopened = Assert.Throws<AnalyticsConfigurationException>(() => AnalyticsEngine.Open(directory).Dispose());
    Assert.That(reopened.Message, Does.Contain("time zone"));
    TestEngine.Delete(directory);
    using var defaults = AnalyticsEngine.Open(directory);
    Assert.Multiple(() =>
    {
      Assert.That(defaults.DefaultSite, Is.EqualTo("default"));
      Assert.That(defaults.TimeZone, Is.EqualTo(TimeZoneInfo.Utc));
    });
  }

  [Test]
  public async Task TrackAsyncQueuesEventsAndReportsWhatItDidNot()
  {
    var engine = TestEngine.Create(directory, o => o.EventFilters.Add(EventFilter.Create(e => e.Name != "ignored")));
    try
    {
      var queued = await engine.TrackAsync(new AnalyticsEvent("signup") { VisitorId = "1", Timestamp = Utc(Day0) });
      var filtered = await engine.TrackAsync(new AnalyticsEvent("ignored") { VisitorId = "1", Timestamp = Utc(Day0) });
      await engine.FlushAsync();
      Assert.Multiple(() =>
      {
        Assert.That(queued, Is.True);
        Assert.That(filtered, Is.False);
        Assert.That(Events(engine, "signup", Day(Day0)), Is.EqualTo(1));
        Assert.That(engine.GetStatistics().EventsFiltered, Is.EqualTo(1));
        Assert.ThrowsAsync<ArgumentException>(async () => await engine.TrackAsync(new AnalyticsEvent("$reserved")));
      });
    }
    finally
    {
      await engine.DisposeAsync();
    }
    Assert.That(await engine.TrackAsync(new AnalyticsEvent("signup")), Is.False, "a disposed engine accepts nothing");
  }

  /// <summary>
  /// An engine with one worker and a queue of one event, whose worker blocks on the first
  /// "block" event until the gate opens: the queue is then full after one more event.
  /// </summary>
  sealed class BlockedEngine : IDisposable
  {
    readonly ManualResetEventSlim gate = new();

    readonly ManualResetEventSlim entered = new();

    public BlockedEngine(string directory, QueueFullBehavior behavior)
    {
      Engine = TestEngine.Create(directory, o =>
      {
        o.Ingestion.WorkerCount = 1;
        o.Ingestion.QueueCapacity = 1;
        o.Ingestion.QueueFullBehavior = behavior;
        o.Enrichers.Add(EventEnricher.Create(context =>
        {
          if (context.EventName != "block" || entered.IsSet)
            return;
          entered.Set();
          gate.Wait();
        }));
      });
      Assert.That(Engine.Track(new AnalyticsEvent("block") { VisitorId = "1", Timestamp = Utc(Day0) }), Is.True);
      Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the worker took the first event");
      Assert.That(Engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(Day0) }), Is.True, "the queue has room for one");
    }

    public AnalyticsEngine Engine { get; }

    public void Release() => gate.Set();

    public void Dispose()
    {
      Release();
      Engine.Dispose();
      gate.Dispose();
      entered.Dispose();
    }
  }

  [Test]
  public void TrackDropsEventsWhenTheQueueIsFull()
  {
    using var blocked = new BlockedEngine(directory, QueueFullBehavior.Drop);
    var accepted = blocked.Engine.Track(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(Day0) });
    blocked.Release();
    blocked.Engine.Flush();
    Assert.Multiple(() =>
    {
      Assert.That(accepted, Is.False);
      Assert.That(blocked.Engine.GetStatistics().EventsDropped, Is.EqualTo(1));
      Assert.That(Events(blocked.Engine, "e", Day(Day0)), Is.EqualTo(1));
    });
  }

  [Test]
  public async Task TrackAsyncWaitsForQueueSpace()
  {
    using var blocked = new BlockedEngine(directory, QueueFullBehavior.Drop);
    // TrackAsync waits whatever the configured behavior, until there is space or it is cancelled.
    using (var cancellation = new CancellationTokenSource())
    {
      var cancelled = blocked.Engine.TrackAsync(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(Day0) }, cancellation.Token).AsTask();
      await Task.Delay(100);
      Assert.That(cancelled.IsCompleted, Is.False, "the queue is full");
      await cancellation.CancelAsync();
      Assert.CatchAsync<OperationCanceledException>(async () => await cancelled);
    }
    var waiting = blocked.Engine.TrackAsync(new AnalyticsEvent("e") { VisitorId = "1", Timestamp = Utc(Day0) }).AsTask();
    await Task.Delay(100);
    Assert.That(waiting.IsCompleted, Is.False);
    blocked.Release();
    Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(10)), Is.True);
    await blocked.Engine.FlushAsync();
    Assert.Multiple(() =>
    {
      Assert.That(Events(blocked.Engine, "e", Day(Day0)), Is.EqualTo(2));
      Assert.That(blocked.Engine.GetStatistics().EventsDropped, Is.Zero);
    });
  }

  [Test]
  public void QueriesCompareWithACustomPeriod()
  {
    using var engine = TestEngine.Create(directory);
    var other = Day0.AddDays(-3);
    foreach (var (day, count) in new[] { (Day0, 3), (other, 2) })
    {
      for (var i = 0; i < count; ++i)
        engine.Track(new AnalyticsEvent("e") { VisitorId = "v" + i, Timestamp = Utc(day.AddHours(10 + i)) });
    }
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "e",
      Range = Day(Day0),
      Compare = ComparisonPeriod.Custom(Day(other)),
      Metrics = { Metric.Events }
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(3));
      Assert.That(result.Totals.Previous[Metric.Events], Is.EqualTo(2));
      Assert.That(result.Comparison.From, Is.EqualTo(Utc(other)));
      Assert.That(result.Totals.Change(Metric.Events), Is.EqualTo(0.5).Within(1e-9));
    });
  }
}
