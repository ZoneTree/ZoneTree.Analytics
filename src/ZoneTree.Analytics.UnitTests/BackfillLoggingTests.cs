using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZoneTree.Analytics.AspNetCore;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class BackfillLoggingTests : DirectoryTest
{
  static void Configure(AnalyticsOptions options)
  {
    options.Sessions.Enabled = false;
    options.Rollups.Granularities = Granularity.Day;
    options.Ingestion.WorkerCount = 1;
  }

  static void Indexes(AnalyticsOptions options)
  {
    Configure(options);
    options.Storage.DimensionIndexes.Dimensions.AddRange(["source", "tag"]);
    options.Storage.RollupIndexes.Definitions.AddRange([new("source", "country"), new("country", "source")]);
  }

  void Seed()
  {
    using var engine = TestEngine.Create(directory, Configure);
    for (var i = 0; i < 3; i++)
    {
      var item = new AnalyticsEvent("pageview")
      {
        VisitorId = "v" + i, Timestamp = Utc(DateTime.UtcNow.Date.AddDays(-2).AddMinutes(i)), Url = "/blog",
        Dimensions = { ["source"] = "GitHub", ["country"] = i == 1 ? "US" : "DE" }
      };
      if (i == 0) item.WithDimension("tag", "featured");
      Assert.That(engine.Track(item), Is.True);
    }
    engine.Flush();
  }

  [Test]
  public void MigrationLogsChecksBuildDurationsAndActualCopiesThenCleanRestartOnlyChecks()
  {
    Seed();
    using var provider = new LogProvider();
    using var factory = LoggerFactory.Create(logging => logging.AddProvider(provider));
    void Options(AnalyticsOptions options) { Indexes(options); options.UseLogger(factory.CreateLogger("Migration")); }
    using (var engine = TestEngine.Create(directory, Options))
    {
      foreach (var stage in new[] { "Dimension index backfill", "Joint rollup backfill", "Rollup index backfill" })
      {
        Assert.That(provider.Messages.Any(log => log.Message.StartsWith(stage + " started;", StringComparison.Ordinal)), Is.True);
        var completed = Completed(provider, stage);
        Assert.That(completed, Does.Contain("directory=").And.Contain("throughput="));
        Assert.That(Number(completed, "duration"), Is.GreaterThanOrEqualTo(0));
        Assert.That(Number(completed, "throughput"), Is.GreaterThan(0));
      }
      var dimensions = Completed(provider, "Dimension index backfill");
      Assert.That(Number(dimensions, "events"), Is.EqualTo(3));
      Assert.That(Number(dimensions, "index rows written"), Is.EqualTo(4), "Missing dimensions do not create index copies.");
      Assert.That(Number(dimensions, "payload bytes copied"), Is.GreaterThan(0));
      var joint = Completed(provider, "Joint rollup backfill");
      Assert.That(Number(joint, "definitions"), Is.EqualTo(1), "Reverse indexes share one authoritative joint definition.");
      Assert.That(Number(joint, "events"), Is.EqualTo(3));
      Assert.That(Number(joint, "sites"), Is.EqualTo(1));
      Assert.That(Number(joint, "flushes"), Is.EqualTo(1));
      Assert.That(Number(joint, "rollup row updates"), Is.EqualTo(2));
      var copies = Completed(provider, "Rollup index backfill");
      Assert.That(Number(copies, "indexed source rows"), Is.EqualTo(2));
      Assert.That(Number(copies, "index rows written"), Is.EqualTo(4));
      Assert.That(copies, Does.Contain("historical backfill=True"));
      AssertChecks(provider, "rebuild required");
      // Opening only decides the work: the builds run in the background.
      Assert.That(Completed(provider, "Rollup index initialization"), Does.Contain("decision=rebuild in background"));
      Assert.That(Completed(provider, "Dimension index initialization"), Does.Contain("decision=rebuild in background"));
      TestContext.Out.WriteLine(string.Join(Environment.NewLine, provider.Messages.Select(log => log.Message).Where(message => message.Contains("backfill", StringComparison.Ordinal))));
    }
    provider.Messages.Clear();
    using var reopened = TestEngine.Create(directory, Options);
    AssertChecks(provider, "reuse existing");
    Assert.That(provider.Messages.Any(log => Regex.IsMatch(log.Message, @"^(Dimension index|Joint rollup|Rollup index) backfill started;")), Is.False);
    Assert.That(provider.Messages.All(log => log.Level == LogLevel.Information), Is.True);
  }

  [Test]
  public void DisabledAndEmptyIndexesExplainChecksAndReportZeroWork()
  {
    using var provider = new LogProvider();
    using var factory = LoggerFactory.Create(logging => logging.AddProvider(provider));
    using (var engine = TestEngine.Create(directory, options => { Configure(options); options.UseLogger(factory.CreateLogger("Empty")); }))
      AssertChecks(provider, "disabled");
    provider.Messages.Clear();
    using var indexed = TestEngine.Create(directory, options => { Indexes(options); options.UseLogger(factory.CreateLogger("Empty")); });
    Assert.That(Completed(provider, "Joint rollup backfill check"), Does.Contain("decision=reuse existing rollups"));
    foreach (var stage in new[] { "Dimension index backfill", "Rollup index backfill" })
    {
      var completed = Completed(provider, stage);
      Assert.That(Number(completed, "throughput"), Is.Zero);
      Assert.That(Number(completed, stage == "Rollup index backfill" ? "rollup rows scanned" : "events"), Is.Zero);
    }
  }

  [Test]
  public void ChangedPartitionManifestAndPendingHistoryExplainRecovery()
  {
    Seed();
    using (var engine = TestEngine.Create(directory, Indexes)) { }
    var meta = new MetaStore(Path.Combine(directory, "meta"));
    meta.Set("rollup-index.backfill-pending", "country,source");
    var key = meta.ListByPrefix("dimension-index.partitions.").First().Key;
    meta.Set(key, "missing-partition");
    using var provider = new LogProvider();
    using var factory = LoggerFactory.Create(logging => logging.AddProvider(provider));
    using var recovered = TestEngine.Create(directory, options => { Indexes(options); options.UseLogger(factory.CreateLogger("Recovery")); });
    Assert.That(Completed(provider, "Dimension index backfill check"), Does.Contain("partition manifest changed"));
    Assert.That(Number(Completed(provider, "Joint rollup backfill check"), "pending definitions"), Is.EqualTo(1));
    Assert.That(Completed(provider, "Rollup index backfill"), Does.Contain("historical backfill=True"));
    Assert.That(recovered.GetStatistics().Errors, Is.Zero);
  }

  [Test]
  public void HostRegistrationAndManuallyCreatedSampleEnginesUseHostLoggingFilters()
  {
    using var provider = new LogProvider();
    using var factory = LoggerFactory.Create(logging => logging.AddProvider(provider).AddFilter("Suppressed", LogLevel.Warning));
    using (var services = new ServiceCollection().AddSingleton<ILoggerFactory>(factory)
        .AddZoneTreeAnalytics(options => { options.DataDirectory = directory; TestEngine.UseTestDefaults(options); Indexes(options); })
        .BuildServiceProvider())
    {
      services.GetRequiredService<AnalyticsEngine>().WaitForIndexesAsync().GetAwaiter().GetResult();
      Assert.That(provider.Messages.Any(log => log.Category == "ZoneTree.Analytics" && log.Message.StartsWith("Rollup index backfill started;", StringComparison.Ordinal)), Is.True);
    }
    provider.Messages.Clear();
    var options = DashboardAnalyticsOptions.CreateBenchmark(directory, TimeZoneInfo.Utc, 1, false, factory.CreateLogger("SampleBenchmark"));
    TestEngine.UseTestDefaults(options);
    using (var engine = new AnalyticsEngine(options))
      Assert.That(provider.Messages.Any(log => log.Category == "SampleBenchmark" && log.Message.Contains("backfill check completed;", StringComparison.Ordinal)), Is.True);
    provider.Messages.Clear();
    options.UseLogger(factory.CreateLogger("Suppressed"));
    using var suppressed = new AnalyticsEngine(options);
    Assert.That(provider.Messages, Is.Empty);
  }

  [Test]
  public void ShutdownLogsWhatItWaitsForAndHowLong()
  {
    var logger = new IndexCrashRecoveryTests.RecoveryLogger();
    var engine = TestEngine.Create(directory, options => { Configure(options); options.Sessions.Enabled = true; options.Storage.Logger = logger; });
    for (var i = 0; i < 3; i++)
      Assert.That(engine.Track(new AnalyticsEvent("pageview") { VisitorId = "v" + i, Timestamp = Utc(DateTime.UtcNow.AddMinutes(-i)) }), Is.True);
    engine.Dispose();
    var messages = logger.Messages.ToArray();
    string Completed(string stage) => messages.Single(message => message.StartsWith(stage + " completed;", StringComparison.Ordinal));
    Assert.Multiple(() =>
    {
      Assert.That(messages.Single(message => message.StartsWith("Shutdown started;", StringComparison.Ordinal)), Does.Contain("index maintenance=none"));
      Assert.That(Completed("Ingestion shutdown"), Does.Contain("events processed=3;").And.Contain("flushes=1"));
      Assert.That(messages.Where(message => message.StartsWith("Partition store shutdown completed;", StringComparison.Ordinal)),
          Has.Some.Contains("closed partitions=1").And.Some.Contains(Path.Combine("rollups", "day")));
      Assert.That(Completed("Session store shutdown"), Does.Contain("duration="));
      Assert.That(Number(Completed("Shutdown"), "duration"), Is.GreaterThanOrEqualTo(Number(Completed("Ingestion shutdown"), "duration")));
      Assert.That(Array.FindLastIndex(messages, message => message.StartsWith("Shutdown completed;", StringComparison.Ordinal)), Is.EqualTo(messages.Length - 1));
    });
  }

  [Test]
  public void FailedStageReportsElapsedTimeAndThrowingLogProviderDoesNotMaskFailure()
  {
    using var provider = new LogProvider();
    using var factory = LoggerFactory.Create(logging => logging.AddProvider(provider));
    var logger = new LoggerAdapter(factory.CreateLogger("Failure"));
    using (var stage = new StageLog(logger, "Dimension index backfill", directory)) { }
    Assert.That(provider.Messages.Last().Message, Does.StartWith("Dimension index backfill failed;").And.Contain("duration="));
    Assert.That(provider.Messages.Any(log => log.Message.StartsWith("Dimension index backfill completed;", StringComparison.Ordinal)), Is.False);
    provider.Throw = true;
    Assert.That(() =>
    {
      using var stage = new StageLog(logger, "Joint rollup backfill", directory);
      throw new IOException("original storage failure");
    }, Throws.TypeOf<IOException>().With.Message.EqualTo("original storage failure"));
  }

  static void AssertChecks(LogProvider provider, string decision)
  {
    foreach (var stage in new[] { "Dimension index backfill check", "Joint rollup backfill check", "Rollup index backfill check" })
    {
      Assert.That(provider.Messages.Any(log => log.Message.StartsWith(stage + " started;", StringComparison.Ordinal)), Is.True);
      var completed = Completed(provider, stage);
      Assert.That(completed, Does.Contain("decision=" + decision));
      Assert.That(Number(completed, "duration"), Is.GreaterThanOrEqualTo(0));
    }
  }

  static string Completed(LogProvider provider, string stage) =>
      provider.Messages.Single(log => log.Message.StartsWith(stage + " completed;", StringComparison.Ordinal)).Message;

  static double Number(string message, string metric) =>
      double.Parse(Regex.Match(message, @"(?:^|; )" + Regex.Escape(metric) + @"=([0-9.]+)").Groups[1].Value, CultureInfo.InvariantCulture);

  sealed class LogProvider : ILoggerProvider
  {
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Messages { get; } = new();
    public bool Throw { get; set; }
    public ILogger CreateLogger(string categoryName) => new CapturedLogger(this, categoryName);
    public void Dispose() { }

    sealed class CapturedLogger(LogProvider owner, string category) : ILogger
    {
      public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
      {
        if (owner.Throw) throw new InvalidOperationException("logging provider failure");
        owner.Messages.Enqueue((category, logLevel, formatter(state, exception)));
      }
    }
  }
}
