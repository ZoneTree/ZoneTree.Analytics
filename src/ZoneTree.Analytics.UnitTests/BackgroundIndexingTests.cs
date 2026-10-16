using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Index builds and history reconstructions run after the engine opens, while it ingests events
/// and answers queries with the same results and notices.
/// </summary>
public sealed class BackgroundIndexingTests : DirectoryTest
{
  static DateTime Start => DateTime.UtcNow.Date.AddDays(-5);

  static void View(AnalyticsEngine engine, string visitor, DateTime at, string page, string source) =>
      Assert.That(engine.Track(new AnalyticsEvent(StandardEvents.PageView)
      {
        VisitorId = visitor, Timestamp = Utc(at), Url = page, UserAgent = UserAgents.ChromeWindows,
        Dimensions = { ["source"] = source, ["country"] = "DE" }
      }), Is.True);

  /// <summary>Grouped by country, a query rollups answer; by page, one that scans raw events.</summary>
  static AnalyticsQuery Query(QuerySource source = QuerySource.Auto, string group = "country")
  {
    var query = new AnalyticsQuery
    {
      Range = Range(Start.AddDays(-1), Start.AddDays(1)), Source = source, Filters = { Filter.Equal("source", "GitHub") },
      GroupBy = { group }, Metrics = { Metric.Events, Metric.Visitors }
    };
    // Session metrics group by session-level dimensions only.
    if (group != "page")
      query.Metrics.AddRange([Metric.Sessions, Metric.BounceRate, Metric.AverageSessionDuration]);
    return query;
  }

  static void Same(AnalyticsResult result, AnalyticsResult raw) =>
      Assert.That(result.Rows.Select(row => string.Join('|', row.Dimensions) + "=" + string.Join('|', row.Values)),
          Is.EqualTo(raw.Rows.Select(row => string.Join('|', row.Dimensions) + "=" + string.Join('|', row.Values))));

  /// <summary>
  /// Opens an engine whose background work waits at the start of a stage. Fails when opening
  /// waits for that work.
  /// </summary>
  static AnalyticsEngine OpenWaitingAt(string directory, string stage, ManualResetEventSlim gate, Action<AnalyticsOptions> configure) =>
      OpenWaitingAt(directory, stage, gate, configure, out _);

  static AnalyticsEngine OpenWaitingAt(string directory, string stage, ManualResetEventSlim gate, Action<AnalyticsOptions> configure,
      out IndexCrashRecoveryTests.RecoveryLogger log)
  {
    var logger = log = new IndexCrashRecoveryTests.RecoveryLogger(message =>
    {
      if (message.StartsWith(stage + " started;", StringComparison.Ordinal))
        gate.Wait(TimeSpan.FromSeconds(30));
    });
    var options = TestEngine.Options(directory, options => { configure(options); options.Storage.Logger = logger; });
    var opening = Task.Run(() => new AnalyticsEngine(options));
    // Before: opening the engine ran the stage itself, so it waited for the gate.
    Assert.That(opening.Wait(TimeSpan.FromSeconds(10)), Is.True, "Opening waited for the background work.");
    return opening.Result;
  }

  [Test]
  public void IndexesAreBuiltInTheBackgroundWhileTheEngineIngestsAndAnswersQueries()
  {
    void Rollups(AnalyticsOptions options) => options.Rollups.Definitions.Add(new("source", "country"));
    using (var seeded = TestEngine.Create(directory, Rollups))
    {
      for (var i = 0; i < 6; ++i)
        View(seeded, "v" + i, Start.AddMinutes(i * 40), "/docs", i % 2 == 0 ? "GitHub" : "Google");
      seeded.Flush();
    }
    using var gate = new ManualResetEventSlim();
    var engine = OpenWaitingAt(directory, "Dimension index backfill", gate, options =>
    {
      Rollups(options);
      options.Storage.DimensionIndexes.Dimensions.Add("source");
      options.Storage.RollupIndexes.Definitions.Add(new("source", "country"));
    });
    try
    {
      Assert.That(engine.GetStatistics().IndexMaintenance, Is.EqualTo("dimension indexes"));
      // Ingestion writes its own copies while the indexes are built.
      View(engine, "late", Start.AddMinutes(30), "/late", "GitHub");
      engine.Flush();
      var raw = engine.Query(Query(QuerySource.RawEvents));
      var scanned = engine.Query(Query(group: "page"));
      var rollups = engine.Query(Query());
      Assert.Multiple(() =>
      {
        Assert.That(raw.Totals[Metric.Events], Is.EqualTo(4));
        Assert.That(scanned.Plan.UsedDimensionIndexes, Is.False);
        Assert.That(scanned.Plan.Notices, Is.EqualTo(new[] { DimensionIndexStore.UnbuiltNotice("source") }));
        Assert.That(scanned.Plan.IsComplete, Is.True, "Notices do not make a result incomplete.");
        Same(scanned, engine.Query(Query(QuerySource.RawEvents, "page")));
        Assert.That(rollups.Plan.UsedRollupIndexes, Is.False);
        Assert.That(rollups.Plan.Notices.Single(), Does.Contain("rollup index on 'source' is being built"));
        Same(rollups, raw);
      });
      gate.Set();
      engine.WaitForIndexesAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
      var indexed = engine.Query(Query(group: "page"));
      var indexedRollups = engine.Query(Query());
      Assert.Multiple(() =>
      {
        Assert.That(engine.GetStatistics().IndexMaintenance, Is.Null);
        Assert.That(indexed.Plan.UsedDimensionIndexes, Is.True);
        Assert.That(indexedRollups.Plan.UsedRollupIndexes, Is.True);
        Assert.That(indexed.Plan.Notices, Is.Empty);
        Assert.That(indexedRollups.Plan.Notices, Is.Empty);
        Same(indexed, engine.Query(Query(QuerySource.RawEvents, "page")));
        Same(indexedRollups, raw);
      });
    }
    finally
    {
      gate.Set();
      engine.Dispose();
    }
  }

  [Test]
  public void JointHistoryIsRebuiltWhileIngestionWritesToTheSameRows()
  {
    using (var seeded = TestEngine.Create(directory))
    {
      View(seeded, "a", Start.AddMinutes(1), "/docs", "GitHub");
      View(seeded, "a", Start.AddMinutes(2), "/pricing", "Google");
      View(seeded, "b", Start.AddMinutes(4), "/docs", "Google");
      seeded.Flush();
    }
    void Indexes(AnalyticsOptions options)
    {
      foreach (var group in new[] { "country", "page" })
        options.Storage.RollupIndexes.Definitions.Add(new("source", group));
    }
    using var gate = new ManualResetEventSlim();
    var engine = OpenWaitingAt(directory, "Joint rollup backfill", gate, Indexes);
    try
    {
      Assert.That(engine.GetStatistics().IndexMaintenance, Is.EqualTo("joint rollup history"));
      // A late event that continues session a, and a new visitor: ingestion writes them to the
      // rows the reconstruction rebuilds, and the reconstruction leaves them out.
      View(engine, "a", Start.AddMinutes(3), "/continue", "Google");
      View(engine, "c", Start.AddMinutes(10), "/docs", "GitHub");
      engine.Flush();
      var raw = engine.Query(Query(QuerySource.RawEvents));
      var during = engine.Query(Query());
      Assert.Multiple(() =>
      {
        // The source of a session applies to all its events.
        Assert.That(raw.Totals[Metric.Events], Is.EqualTo(4));
        Assert.That(during.Plan.UsedRollupIndexes, Is.False);
        Assert.That(during.Plan.Notices, Has.Some.Contains("rollup history of [country, source] is being rebuilt"));
        Same(during, raw);
      });
      gate.Set();
      engine.WaitForIndexesAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
      var rebuilt = engine.Query(Query());
      var pages = engine.Web.GetPages(new WebReportRequest { Range = Day(Start), Filters = { Filter.Equal("source", "GitHub") } });
      Assert.Multiple(() =>
      {
        Assert.That(rebuilt.Plan.UsedRollupIndexes, Is.True);
        Assert.That(rebuilt.Plan.UsedRawEvents, Is.False);
        Assert.That(rebuilt.Plan.Notices, Is.Empty);
        Same(rebuilt, raw);
        Assert.That(engine.Web.GetSummary(new WebReportRequest { Range = Day(Start) }).Summary.PageViews, Is.EqualTo(5));
        Assert.That(pages.Plan.UsedRawEvents, Is.False);
        Assert.That(pages.Rows.Single(row => row.Page == "/docs").Entrances, Is.EqualTo(2));
      });
    }
    finally
    {
      gate.Set();
      engine.Dispose();
    }
    using var reopened = TestEngine.Create(directory, Indexes);
    Same(reopened.Query(Query()), reopened.Query(Query(QuerySource.RawEvents)));
  }

  [Test]
  public void ABuildStoppedByDisposalResumesWhenTheEngineOpensAgain()
  {
    using (var seeded = TestEngine.Create(directory))
    {
      View(seeded, "a", Start, "/docs", "GitHub");
      seeded.Flush();
    }
    void Indexes(AnalyticsOptions options) => options.Storage.DimensionIndexes.Dimensions.Add("source");
    using var gate = new ManualResetEventSlim();
    var engine = OpenWaitingAt(directory, "Dimension index backfill", gate, Indexes, out var stopped);
    var waiting = engine.WaitForIndexesAsync();
    var disposal = Task.Run(engine.Dispose);
    gate.Set();
    Assert.That(disposal.Wait(TimeSpan.FromSeconds(30)), Is.True);
    Assert.That(stopped.Messages, Has.Some.StartsWith("Index maintenance shutdown completed;").And.Some.Contains("running=dimension indexes"));
    Assert.That(() => waiting.GetAwaiter().GetResult(), Throws.InstanceOf<OperationCanceledException>());
    Assert.That(new MetaStore(Path.Combine(directory, "meta")).Get("dimension-index.clean"), Is.EqualTo("false"),
        "An unfinished build does not claim coverage.");
    using var reopened = TestEngine.Create(directory, Indexes);
    var result = reopened.Query(Query(group: "page"));
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
  }

  static void JointIndex(AnalyticsOptions options) => options.Storage.RollupIndexes.Definitions.Add(new("source", "country"));

  void SeedJointHistory()
  {
    using var seeded = TestEngine.Create(directory);
    View(seeded, "a", Start.AddMinutes(1), "/docs", "GitHub");
    View(seeded, "a", Start.AddMinutes(2), "/pricing", "Google");
    View(seeded, "b", Start.AddMinutes(4), "/docs", "GitHub");
    seeded.Flush();
  }

  [Test]
  public void AJournalLeftByACompletedReconstructionIsNotResumedWhenItsDefinitionsReturn()
  {
    SeedJointHistory();
    // The journal as a reconstruction leaves it when the process dies right after it completes.
    var journal = Path.Combine(directory, "backfill", "joint-rollups");
    Dictionary<string, byte[]> leftover = null;
    var logger = new IndexCrashRecoveryTests.RecoveryLogger(message =>
    {
      if (leftover == null && message.StartsWith("Joint rollup backfill progress;", StringComparison.Ordinal) && message.Contains("checkpoint saved;", StringComparison.Ordinal))
        leftover = Directory.GetFiles(journal).ToDictionary(path => path, File.ReadAllBytes);
    });
    using (TestEngine.Create(directory, options => { JointIndex(options); options.Storage.Logger = logger; })) { }
    Assert.That(leftover, Is.Not.Empty);
    // Removing the index forgets the definition; an event arrives while it is gone.
    using (var removed = TestEngine.Create(directory))
    {
      View(removed, "c", Start.AddMinutes(30), "/docs", "GitHub");
      removed.Flush();
    }
    foreach (var (path, bytes) in leftover)
      File.WriteAllBytes(path, bytes);
    using var readded = TestEngine.Create(directory, JointIndex);
    var result = readded.Query(Query());
    // Before: the journal resumed, kept the rows of the earlier activation and missed event c.
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
    Same(result, readded.Query(Query(QuerySource.RawEvents)));
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(4), "The source of session a applies to both its events.");
  }

  [Test]
  public void HistoryWaitingForItsFoldIsNotReportedAsRebuildingOnceTheIndexIsRemoved()
  {
    SeedJointHistory();
    using var gate = new ManualResetEventSlim();
    var engine = OpenWaitingAt(directory, "Rollup index backfill", gate, JointIndex);
    // The reconstruction completes; the rollup index build that folds it in never runs.
    Assert.That(SpinWait.SpinUntil(() => engine.GetStatistics(includePartitions: false).IndexMaintenance == "rollup indexes",
        TimeSpan.FromSeconds(30)), Is.True);
    var disposal = Task.Run(engine.Dispose);
    gate.Set();
    Assert.That(disposal.Wait(TimeSpan.FromSeconds(30)), Is.True);
    // The joint definition stays, without its index.
    using var reopened = TestEngine.Create(directory, options => options.Rollups.Definitions.Add(new("source", "country")));
    var result = reopened.Query(Query());
    // Before: nothing folds the history in any more, but every query reported it as being rebuilt.
    Assert.That(result.Plan.Notices, Is.Empty);
    Same(result, reopened.Query(Query(QuerySource.RawEvents)));
  }

  static void DimensionIndex(AnalyticsOptions options) => options.Storage.DimensionIndexes.Dimensions.Add("source");

  [Test]
  public void ARepairKeepsTheCopiesIngestionWritesMeanwhile()
  {
    using (var seeded = TestEngine.Create(directory, DimensionIndex))
    {
      View(seeded, "a", Start.AddMinutes(1), "/docs", "GitHub");
      seeded.Flush();
    }
    // The writes of that day were not reconciled when the previous run ended.
    new MetaStore(Path.Combine(directory, "meta")).Set("dimension-index.dirty." + Start.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), "true");
    using var gate = new ManualResetEventSlim();
    var engine = OpenWaitingAt(directory, "Dimension index backfill", gate, DimensionIndex);
    try
    {
      // Ingestion copies a new event of the day while the repair waits.
      View(engine, "b", Start.AddHours(1), "/docs", "GitHub");
      engine.Flush();
      gate.Set();
      engine.WaitForIndexesAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
      var result = engine.Query(Query(group: "page"));
      // Before: the repair deleted the new copy and copied the events stored before the engine opened only.
      Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(2));
      Same(result, engine.Query(Query(QuerySource.RawEvents, "page")));
    }
    finally
    {
      gate.Set();
      engine.Dispose();
    }
  }

  [Test]
  public void HistoryWaitingForItsFoldIsFoldedInWhenItsIndexIsRemovedAndItsRollupsRemain()
  {
    SeedJointHistory();
    using var gate = new ManualResetEventSlim();
    var engine = OpenWaitingAt(directory, "Rollup index backfill", gate, options =>
    {
      JointIndex(options);
      options.Storage.RollupIndexes.Definitions.Add(new("source", "page"));
    });
    // Both histories are rebuilt; the rollup index build that folds them in stops before it starts.
    Assert.That(SpinWait.SpinUntil(() => engine.GetStatistics(includePartitions: false).IndexMaintenance == "rollup indexes",
        TimeSpan.FromSeconds(30)), Is.True);
    var disposal = Task.Run(engine.Dispose);
    gate.Set();
    Assert.That(disposal.Wait(TimeSpan.FromSeconds(30)), Is.True);
    // One index goes; its joint rollups stay.
    using var reopened = TestEngine.Create(directory, options =>
    {
      JointIndex(options);
      options.Rollups.Definitions.Add(new("source", "page"));
    });
    var result = reopened.Query(Query(group: "page"));
    // Before: its history was published without being folded in, so the rollups read nothing.
    Assert.That(result.Plan.UsedRollups, Is.True);
    Assert.That(result.Plan.UsedRawEvents, Is.False);
    Same(result, reopened.Query(Query(QuerySource.RawEvents, "page")));
  }

  [Test]
  public void ABuildWhoseCopyFailedIsNotRecordedAsComplete()
  {
    using (var seeded = TestEngine.Create(directory))
    {
      for (var i = 0; i < 3; ++i)
        View(seeded, "v" + i, Start.AddMinutes(i), "/docs", "GitHub");
      seeded.Flush();
    }
    var opens = 0;
    // The dimension index build's first copy fails; a joint rollup index is built after it.
    using (var engine = new AnalyticsEngine(TestEngine.Options(directory, options =>
    {
      DimensionIndex(options);
      JointIndex(options);
      options.Storage.DimensionIndexes.Store.ConfigureMaintainer = _ =>
      {
        if (++opens == 1) throw new IOException("Injected dimension index copy failure");
      };
    })))
    {
      // Before: the retry found the indexes invalid, returned, and readiness reported success.
      var failure = Assert.ThrowsAsync<AnalyticsStorageException>(() => engine.WaitForIndexesAsync().WaitAsync(TimeSpan.FromSeconds(30)));
      var rollups = engine.Query(Query());
      Assert.Multiple(() =>
      {
        Assert.That(failure.InnerException, Is.TypeOf<IOException>());
        Assert.That(engine.GetStatistics().IndexMaintenance, Is.Null);
        Assert.That(engine.Query(Query(group: "page")).Plan.UsedDimensionIndexes, Is.False);
        // The jobs after the failed one still ran.
        Assert.That(rollups.Plan.UsedRollupIndexes, Is.True);
        Same(rollups, engine.Query(Query(QuerySource.RawEvents)));
      });
    }
    // Before: the retry skipped every copy and recorded the indexes as clean.
    Assert.That(new MetaStore(Path.Combine(directory, "meta")).Get("dimension-index.clean"), Is.EqualTo("false"));
    using var reopened = TestEngine.Create(directory, DimensionIndex);
    var result = reopened.Query(Query(group: "page"));
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(3));
  }

  [Test]
  public void QueriesSeeTheRawEventsRetentionHolds()
  {
    var options = TestEngine.Options(directory, options => options.Retention.RawEvents = TimeSpan.FromDays(2));
    var clock = new RetentionClock(options.Retention, new ReportingCalendar(options.TimeZone, options.FirstDayOfWeek));
    var now = DateTime.UtcNow.Ticks;
    var held = DateTime.UtcNow.Date.AddDays(-10).Ticks;
    var cutoff = clock.At(now).RawEvents;
    clock.HoldRawEvents(held);
    Assert.Multiple(() =>
    {
      // Before: queries computed the cutoff from the options, so held events were reported as outside retention.
      Assert.That(clock.At(now).RawEvents, Is.EqualTo(held));
      Assert.That(clock.Current.RawEvents, Is.EqualTo(held));
      Assert.That(clock.At(now).RawEventsWarning(held), Is.Null);
    });
    clock.ReleaseRawEvents();
    Assert.That(clock.At(now).RawEvents, Is.EqualTo(cutoff));
  }
}
