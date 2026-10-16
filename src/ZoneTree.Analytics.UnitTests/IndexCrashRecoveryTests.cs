using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ZoneTree.Analytics.Storage;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.UnitTests;

[NonParallelizable]
public sealed class IndexCrashRecoveryTests : DirectoryTest
{
  internal static DateTime Start => DateTime.UtcNow.Date.AddDays(-20);
  internal const int LargeCount = IndexBackfillCheckpoint.Interval + 5;

  internal static void Configure(AnalyticsOptions options, string scenario, bool indexed)
  {
    // A killed process loses the most recent writes of the default asynchronous write-ahead
    // log, which the engine accepts. These tests check what the checkpoints resume, so every
    // write reaches its log before it returns.
    foreach (var store in new[] { options.Storage.RawEvents, options.Storage.Rollups, options.Storage.Sessions,
        options.Storage.DimensionIndexes.Store, options.Storage.RollupIndexes.Store })
      store.WriteAheadLogMode = ZoneTree.Options.WriteAheadLogMode.Sync;
    options.Ingestion.WorkerCount = 1;
    options.Queries.MaxDegreeOfParallelism = 1;
    options.Sessions.Enabled = scenario.StartsWith("joint", StringComparison.Ordinal);
    options.Rollups.Granularities = Granularity.Hour | Granularity.Day;
    options.Rollups.Definitions.Clear();
    options.Rollups.Definitions.Add(new("source"));
    if (scenario is "rollup" or "idle" or "runtime" or "repair")
      options.Rollups.Definitions.Add(new("source", "country"));
    if (!indexed) return;
    if (scenario is "dimension" or "idle" or "runtime" or "repair")
      options.Storage.DimensionIndexes.Dimensions.AddRange(["source", "country"]);
    if (scenario != "dimension")
      options.Storage.RollupIndexes.Definitions.AddRange([new("source", "country"), new("country", "source")]);
  }

  internal static void View(AnalyticsEngine engine, string visitor, DateTime at) =>
      Assert.That(engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = visitor, Timestamp = Utc(at), Url = "/blog",
        Dimensions = { ["source"] = "GitHub", ["country"] = "DE" }, Values = { ["score"] = 2 }
      }), Is.True);

  void Seed(string scenario)
  {
    using var engine = TestEngine.Create(directory, options => Configure(options, scenario, scenario is "idle" or "runtime" or "repair"));
    if (scenario is "dimension" || scenario.StartsWith("joint", StringComparison.Ordinal))
    {
      View(engine, "seed", Start);
      engine.Flush();
      using var lease = engine.Storage.RawEvents.Acquire(engine.Storage.RawEvents.GetPartitions().Single());
      using (var iterator = lease.ZoneTree.CreateIterator())
      {
        iterator.SeekFirst();
        Assert.That(iterator.Next(), Is.True);
        lease.ZoneTree.ForceDelete(iterator.CurrentKey);
      }
      var dimensions = new Dictionary<string, string> { ["source"] = "GitHub", ["country"] = "DE", ["page"] = "/blog" };
      KeyValuePair<string, double>[] values = [new("score", 2)];
      var varied = scenario is "joint-final" or "joint-expire";
      for (var i = 0; i < (varied ? 5000 : LargeCount); ++i)
      {
        var ticks = Start.AddHours(scenario == "joint-expire" ? i / 2500 : 0).AddTicks(i).Ticks;
        if (varied) dimensions["source"] = "source-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lease.ZoneTree.Upsert(KeyCodec.RawKey("default", ticks, i + 1),
            RawEventCodec.Encode("pageview", ticks, varied ? (ulong)i + 1 : 1, varied ? (ulong)i + 1 : 1,
                varied ? ticks : Start.Ticks, true, dimensions, values));
      }
      lease.Maintainer.EvictToDisk();
      lease.Maintainer.WaitForBackgroundThreads();
    }
    else
    {
      for (var day = 0; day < 12; ++day)
        for (var i = 0; i < 2; ++i) View(engine, $"v{day}-{i}", Start.AddDays(day).AddMinutes(i));
      engine.Flush();
    }
  }

  [TestCase("idle")]
  [TestCase("runtime")]
  [TestCase("dimension")]
  [TestCase("rollup")]
  [TestCase("joint")]
  [TestCase("joint-redo")]
  [TestCase("joint-final")]
  [TestCase("joint-expire")]
  [TestCase("repair")]
  public async Task KilledProcessReusesCompletedIndexesOrResumesDurableWork(string scenario)
  {
    Seed(scenario);
    await KillProbe(scenario);
    if (scenario == "repair") await KillProbe(scenario); // Interrupt the repair as well.
    var logger = new RecoveryLogger();
    using (var engine = TestEngine.Create(directory, options =>
    {
      Configure(options, scenario, true);
      options.Storage.Logger = logger;
    }))
    {
      var query = new AnalyticsQuery
      {
        Range = Range(Start, Start.AddDays(13)), Filters = { Filter.Equal("source", "GitHub") },
        GroupBy = { "country" }, Metrics = { Metric.Events, Metric.Visitors, Metric.Sum("score") }
      };
      var varied = scenario is "joint-final" or "joint-expire";
      if (varied)
      {
        query.Filters.Clear(); query.Filters.Add(Filter.Equal("country", "DE"));
        query.GroupBy.Clear(); query.GroupBy.Add("source");
      }
      if (scenario.StartsWith("joint", StringComparison.Ordinal))
        query.Metrics.AddRange([Metric.Sessions, Metric.BounceRate, Metric.AverageSessionDuration, Metric.PageViewsPerSession]);
      var indexed = engine.Query(query);
      query.Source = QuerySource.RawEvents;
      var raw = engine.Query(query);
      Assert.That(indexed.Totals.Values, Is.EqualTo(raw.Totals.Values));
      Assert.That(indexed.Totals[Metric.Events], Is.EqualTo(varied ? 5000 : scenario is "dimension" || scenario.StartsWith("joint", StringComparison.Ordinal)
          ? LargeCount : scenario is "runtime" or "repair" ? 25 : 24));
      Assert.That(scenario == "dimension" ? indexed.Plan.UsedDimensionIndexes : indexed.Plan.UsedRollupIndexes, Is.True);
      if (scenario == "idle")
        Assert.That(logger.Messages.Any(message => Regex.IsMatch(message, @"^(Dimension index|Joint rollup|Rollup index) backfill started;")), Is.False);
      if (scenario is "runtime" or "repair")
      {
        Assert.That(logger.Messages.Any(message => message.Contains("decision=repair affected days", StringComparison.Ordinal)), Is.True);
        var dimension = logger.Messages.Single(message => message.StartsWith("Dimension index backfill completed;", StringComparison.Ordinal));
        Assert.That(dimension, Does.Contain("events=3;").And.Contain("dirty days=1;"));
        Assert.That(new MetaStore(Path.Combine(directory, "meta")).ListByPrefix("dimension-index.dirty."), Is.Empty);
      }
      if (scenario is "dimension" || scenario.StartsWith("joint", StringComparison.Ordinal))
        Assert.That(logger.Messages.Any(message => message.Contains(scenario == "joint-final" ? "resumed events=5000;" :
            scenario == "joint-expire" ? "resumed events=2500;" : "resumed events=100000;", StringComparison.Ordinal)), Is.True);
      if (scenario == "rollup")
        Assert.That(logger.Messages.Any(message => Regex.IsMatch(message, @"resumed rows=[1-9][0-9]*;")), Is.True);
    }
    logger.Messages.Clear();
    using var reopened = TestEngine.Create(directory, options => { Configure(options, scenario, true); options.Storage.Logger = logger; });
    Assert.That(logger.Messages.Any(message => Regex.IsMatch(message, @"^(Dimension index|Joint rollup|Rollup index) backfill started;")), Is.False);
  }

  [Test]
  public void RolloverAndExpiredPartitionRemovalDoNotInvalidateCompletedCoverage()
  {
    string[] expected = ["2026-01-01", "2026-01-02"];
    Assert.That(IndexRecoveryScan.MissingRetainedPartitions(expected, [.. expected, "2026-01-03"], long.MinValue), Is.False);
    Assert.That(IndexRecoveryScan.MissingRetainedPartitions(expected, ["2026-01-02"], new DateTime(2026, 1, 2).Ticks), Is.False);
    Assert.That(IndexRecoveryScan.MissingRetainedPartitions(expected, ["2026-01-02"], new DateTime(2026, 1, 1).Ticks), Is.True);
    Assert.That(IndexRecoveryScan.MissingRetainedPartitions(expected, ["2026-01-01"], DateTime.MaxValue.Ticks), Is.True);
  }

  [Test]
  public void InterruptedDeletionRemovesOrphanCopiesOnlyInTheAffectedDay()
  {
    Seed("runtime");
    using (var engine = TestEngine.Create(directory, options => Configure(options, "runtime", true)))
    {
      using var lease = engine.Storage.RawEvents.Acquire(engine.Storage.RawEvents.GetPartitions().Single());
      using var iterator = lease.ZoneTree.CreateIterator();
      iterator.Seek(KeyCodec.RawKey("default", Start.AddDays(10).Ticks, 0));
      Assert.That(iterator.Next(), Is.True);
      engine.DimensionIndexes.BeforeWrite(KeyCodec.ReadRawTicks(iterator.CurrentKey.Span));
      lease.ZoneTree.ForceDelete(iterator.CurrentKey);
      engine.DimensionIndexes.Invalidate(); // Prevent graceful shutdown from clearing the interrupted intent.
    }
    var logger = new RecoveryLogger();
    using var recovered = TestEngine.Create(directory, options => { Configure(options, "runtime", true); options.Storage.Logger = logger; });
    var query = new AnalyticsQuery
    {
      Range = Range(Start, Start.AddDays(13)), GroupBy = { "page" },
      Filters = { Filter.Equal("source", "GitHub") }, Metrics = { Metric.Events }
    };
    var result = recovered.Query(query);
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(23));
    Assert.That(logger.Messages.Single(message => message.StartsWith("Dimension index backfill completed;", StringComparison.Ordinal)),
        Does.Contain("events=1;").And.Contain("dirty days=1;"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void CompletedNonDurableBackfillsCannotReuseACursorThatMissesEarlierNewEvents(bool rollup)
  {
    Seed("runtime");
    void ConfigureNonDurable(AnalyticsOptions options)
    {
      Configure(options, "runtime", true);
      (rollup ? options.Storage.RollupIndexes.Store : options.Storage.DimensionIndexes.Store).WriteAheadLogMode = ZoneTree.Options.WriteAheadLogMode.None;
    }
    using (var engine = TestEngine.Create(directory, ConfigureNonDurable))
    {
      View(engine, "late", Start.AddMinutes(5));
      engine.Flush();
    }
    var prefix = rollup ? "rollup-index" : "dimension-index";
    var meta = new MetaStore(Path.Combine(directory, "meta"));
    Assert.That(meta.Get(prefix + ".clean"), Is.EqualTo("false"));
    Assert.That(meta.ListByPrefix(prefix + ".backfill."), Is.Empty);
    using var recovered = TestEngine.Create(directory, ConfigureNonDurable);
    var query = new AnalyticsQuery
    {
      Range = Range(Start, Start.AddDays(13)), GroupBy = { rollup ? "country" : "page" },
      Filters = { Filter.Equal("source", "GitHub") }, Metrics = { Metric.Events }
    };
    var result = recovered.Query(query);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(25));
    Assert.That(rollup ? result.Plan.UsedRollupIndexes : result.Plan.UsedDimensionIndexes, Is.True);
  }

  async Task KillProbe(string scenario)
  {
    var ready = Path.Combine(directory, "crash-ready");
    File.Delete(ready);
    var start = new ProcessStartInfo("dotnet")
    {
      UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("vstest");
    start.ArgumentList.Add(typeof(IndexCrashProbe).Assembly.Location);
    start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=ZoneTree.Analytics.UnitTests.IndexCrashProbe.Run");
    start.ArgumentList.Add("/Logger:console;Verbosity=quiet");
    start.Environment["ANALYTICS_CRASH_TEST_DIRECTORY"] = directory;
    start.Environment["ANALYTICS_CRASH_TEST_SCENARIO"] = scenario;
    using var process = Process.Start(start);
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    Process probe = null;
    try
    {
      var timeout = Stopwatch.StartNew();
      var probeId = 0;
      while (!process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(60))
      {
        try
        {
          if (int.TryParse(File.ReadAllText(ready), out probeId)) break;
        }
        catch (IOException) { } // The marker can exist before its writer releases the handle.
        await Task.Delay(50);
      }
      if (probeId == 0)
      {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        Assert.Fail($"Crash probe did not reach its checkpoint: {await output}\n{await errors}");
      }
      probe = Process.GetProcessById(probeId);
    }
    finally
    {
      if (!process.HasExited) process.Kill(entireProcessTree: true);
      await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
      // Waiting for the dotnet launcher alone does not wait for its testhost child on
      // Windows. The child must finish releasing file handles before the next engine opens.
      if (probe != null)
      {
        await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        probe.Dispose();
      }
      var released = Stopwatch.StartNew();
      while (true)
      {
        try
        {
          foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
          {
            using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
          }
          break;
        }
        catch (IOException) when (released.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(50); }
      }
      await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(15));
    }
  }

  internal sealed class RecoveryLogger(Action<string> intercept = null) : ILogger
  {
    public ConcurrentQueue<string> Messages { get; } = new();
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public void LogError(Exception error) => Messages.Enqueue(error.ToString());
    public void LogWarning(object message) => Messages.Enqueue(message?.ToString());
    public void LogTrace(object message) { }
    public void LogInfo(object message)
    {
      Messages.Enqueue(message?.ToString());
      intercept?.Invoke(message?.ToString());
    }
  }
}

/// <summary>Runs only in an explicitly selected child process, killed by the owning test.</summary>
public sealed class IndexCrashProbe
{
  [Test, Explicit("Child process used by index crash-recovery tests")]
  public void Run()
  {
    var directory = Environment.GetEnvironmentVariable("ANALYTICS_CRASH_TEST_DIRECTORY");
    var scenario = Environment.GetEnvironmentVariable("ANALYTICS_CRASH_TEST_SCENARIO");
    if (string.IsNullOrEmpty(directory)) Assert.Ignore("No crash probe requested.");
    void Block()
    {
      File.WriteAllText(Path.Combine(directory, "crash-ready"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
      Thread.Sleep(Timeout.Infinite);
    }
    var stage = scenario switch
    {
      "dimension" => "Dimension index backfill progress;",
      "rollup" => "Rollup index backfill progress;",
      "joint" or "joint-redo" or "joint-final" or "joint-expire" => "Joint rollup backfill progress;",
      "repair" => "Dimension index backfill progress;",
      _ => ""
    };
    var logger = new IndexCrashRecoveryTests.RecoveryLogger(message =>
    {
      if (stage.Length > 0 && message.StartsWith(stage, StringComparison.Ordinal) &&
          (!(scenario is "joint-final" or "joint-expire") || !message.Contains("sessions=0;", StringComparison.Ordinal)) &&
          message.Contains(scenario switch { "joint-redo" => "checkpoint prepared;", "repair" => "partition repaired;", _ => "checkpoint saved;" }, StringComparison.Ordinal)) Block();
    });
    using var engine = TestEngine.Create(directory, options =>
    {
      IndexCrashRecoveryTests.Configure(options, scenario, true);
      options.Storage.Logger = logger;
    });
    if (scenario is "runtime" or "repair")
    {
      IndexCrashRecoveryTests.View(engine, "added", IndexCrashRecoveryTests.Start.AddDays(10).AddMinutes(2));
      engine.Flush();
      engine.CompactAsync().GetAwaiter().GetResult();
      // Copies can reach disk before the corresponding primary WAL, and vice versa.
      // Leave one missing copy and a ghost in the affected day for recovery to reconcile.
      var index = engine.Storage.GetDimensionIndex("source");
      using var lease = index.Acquire(index.GetPartitions().Single());
      using var iterator = lease.ZoneTree.CreateIterator();
      var prefix = DimensionIndexKeyCodec.Prefix(index.KeyPrefix.Span, "default", "GitHub");
      iterator.Seek(KeyCodec.SeekKey(prefix, IndexCrashRecoveryTests.Start.AddDays(10).Ticks));
      Assert.That(iterator.Next(), Is.True);
      var value = iterator.CurrentValue;
      lease.ZoneTree.ForceDelete(iterator.CurrentKey);
      // The orphan of an event stored earlier, whose raw record an interrupted deletion removed.
      lease.ZoneTree.Upsert(DimensionIndexKeyCodec.Key(index.KeyPrefix.Span, "default", "GitHub",
          IndexCrashRecoveryTests.Start.AddDays(10).AddMinutes(3).Ticks, 1), value);
      lease.Maintainer.EvictToDisk();
      lease.Maintainer.WaitForBackgroundThreads();
    }
    Block();
  }
}
