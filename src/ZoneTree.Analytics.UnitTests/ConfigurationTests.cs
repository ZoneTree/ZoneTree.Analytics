namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// How configuration behaves across the lifetime of an engine and of a data directory.
/// </summary>
public sealed class ConfigurationTests : DirectoryTest
{
  static readonly DateTime T0 = DateTime.UtcNow.Date.AddDays(-2).AddHours(10);

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
  public void WriteAheadLogsCanLiveInTheirOwnDirectory()
  {
    var logs = Path.Combine(directory, "logs");
    void Separate(AnalyticsOptions o)
    {
      o.DataDirectory = Path.Combine(directory, "data");
      o.Storage.WriteAheadLogDirectory = logs;
      o.Storage.Partitioning.RawEvents = 1;
    }
    var old = DateTime.UtcNow.Date.AddDays(-10).AddHours(12);
    using (var engine = TestEngine.Create(directory, Separate))
    {
      // The old event fills the first raw partition; the next one starts a few days later.
      engine.Track(new AnalyticsEvent("purchase") { VisitorId = "old", Timestamp = Utc(old) });
      engine.Flush();
      Assert.That(engine.RollOverPartitions(old.Ticks), Is.EqualTo(1));
      engine.Track(Event("purchase", "pro", 0));
    }
    var oldDay = old.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    Assert.Multiple(() =>
    {
      // Every store but the engine metadata keeps its log under the same relative path.
      Assert.That(Directory.Exists(Path.Combine(logs, "events", oldDay)), Is.True);
      Assert.That(Directory.Exists(Path.Combine(logs, "sessions")), Is.True);
      Assert.That(Directory.Exists(Path.Combine(logs, "meta")), Is.False);
      Assert.Throws<AnalyticsConfigurationException>(() =>
          TestEngine.Create(directory, o => o.DataDirectory = Path.Combine(directory, "data")).Dispose());
    });

    // Retention drops a partition's log together with its data.
    using (var engine = TestEngine.Create(directory, o =>
    {
      Separate(o);
      o.Retention.RawEvents = TimeSpan.FromDays(3);
    }))
    {
      Assert.That(engine.ApplyRetentionCountingDrops(), Is.EqualTo(1));
      Assert.That(engine.Query(new AnalyticsQuery { Event = "purchase", Range = Range, Metrics = { Metric.Events } })
          .Totals[Metric.Events], Is.EqualTo(1));
    }
    Assert.Multiple(() =>
    {
      Assert.That(Directory.Exists(Path.Combine(directory, "data", "events", oldDay)), Is.False);
      Assert.That(Directory.Exists(Path.Combine(logs, "events", oldDay)), Is.False);
    });
  }

  [Test]
  public void StoreOptionsAreValidatedWhenTheEngineStarts()
  {
    var engineRule = Assert.Throws<ArgumentException>(() =>
        TestEngine.Create(directory, o => o.Storage.Rollups.BTreeNodeSize = 0).Dispose());
    var bloomRule = Assert.Throws<ArgumentException>(() =>
        TestEngine.Create(directory, o => o.Storage.Sessions.MutableSegmentBloomFilterBitsPerItem = 65).Dispose());
    // Rules of ZoneTree itself surface at start too, not on the first event.
    var zoneTreeRule = Assert.Catch(() =>
        TestEngine.Create(directory, o => o.Storage.Rollups.DiskSegmentMaxItemCount = 1_000).Dispose());
    Assert.Catch(() => TestEngine.Create(directory, o =>
        o.Storage.RawEvents.ConfigureWriteAheadLog = wal => wal.AsyncCompressedModeOptions = null).Dispose());
    Assert.Multiple(() =>
    {
      Assert.That(engineRule.Message, Does.Contain("Storage.Rollups.BTreeNodeSize"));
      Assert.That(bloomRule.Message, Does.Contain("Storage.Sessions.MutableSegmentBloomFilterBitsPerItem"));
      Assert.That(zoneTreeRule.Message, Does.Contain("DiskSegmentMaxItemCount"));
    });
  }

  [Test]
  public void StoreOptionsReachZoneTree()
  {
    var walModes = new System.Collections.Concurrent.ConcurrentBag<ZoneTree.Options.WriteAheadLogMode>();
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.RawEvents.WriteAheadLogMode = ZoneTree.Options.WriteAheadLogMode.Sync;
      o.Storage.RawEvents.ConfigureWriteAheadLog = wal => walModes.Add(wal.WriteAheadLogMode);
      // Below ZoneTree's recommended minimum: only accepted with AllowUnsafeOptionValues.
      o.Storage.Rollups.DiskSegmentMaxItemCount = 1_000;
      o.Storage.Rollups.AllowUnsafeOptionValues = true;
      o.Storage.Rollups.BTreeLockMode = ZoneTree.Collections.BTree.Lock.BTreeLockMode.NodeLevelMonitor;
    });
    engine.Track(Event("purchase", "pro", 0));
    engine.Flush();
    Assert.That(walModes, Has.Some.EqualTo(ZoneTree.Options.WriteAheadLogMode.Sync));
  }

  [Test]
  public void OptionsAreFixedWhenTheEngineIsCreated()
  {
    var options = TestEngine.Options(directory, o => o.Rollups.Definitions.Add(new RollupDefinition("plan")));
    using var engine = new AnalyticsEngine(options);

    // None of these reach the running engine.
    options.Privacy.DeniedDimensions.Add("plan");
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
