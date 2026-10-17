using ZoneTree.Analytics.Storage;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class ReleaseRegressionTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-2);

  [Test]
  public void EquivalentSitesAndAnonymousInputsShareTheirWorkerAndSession()
  {
    using var engine = TestEngine.Create(directory, o => o.Ingestion.WorkerCount = 4);
    foreach (var site in new[] { null, "default", " default ", "def\0ault" })
    {
      for (var visitor = 0; visitor < 20; ++visitor)
      {
        engine.Track(new AnalyticsEvent("pageview")
        {
          Site = site, VisitorId = "v" + visitor, Url = "/", Timestamp = Utc(Day0.AddHours(1))
        });
      }
      engine.Flush();
    }
    // Empty and missing ids both use anonymous identity; empty and missing user agents hash alike.
    foreach (var (id, ua) in new[] { ((string)null, (string)null), ("", "") })
    {
      engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = id, UserAgent = ua, IpAddress = "192.0.2.1", Url = "/", Timestamp = Utc(Day0.AddHours(1))
      });
      engine.Flush();
    }
    var result = engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Metrics = { Metric.PageViews, Metric.Sessions, Metric.Bounces }
    });
    Assert.Multiple(() =>
    {
      Assert.That(engine.GetStatistics().SessionsStarted, Is.EqualTo(21));
      Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(82));
      Assert.That(result.Totals[Metric.Sessions], Is.EqualTo(21));
      Assert.That(result.Totals[Metric.Bounces], Is.Zero);
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task ErasureClearsCachedAndStoredSession(bool storeRaw)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.StoreRawEvents = storeRaw;
      o.Ingestion.WorkerCount = 4;
    });
    engine.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/before", Timestamp = Utc(Day0) });
    engine.Flush();
    Assert.That(await engine.EraseVisitorAsync("alice", " default "), Is.EqualTo(storeRaw ? 1 : 0));
    engine.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/after", Timestamp = Utc(Day0.AddMinutes(1)) });
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), GroupBy = { StandardDimensions.EntryPage }, Metrics = { Metric.Sessions }
    });
    Assert.Multiple(() =>
    {
      Assert.That(engine.GetStatistics().SessionsStarted, Is.EqualTo(2));
      Assert.That(result.Rows.Select(x => x.Dimensions[0]), Is.EquivalentTo(new[] { "/before", "/after" }));
    });
    Assert.That(engine.EraseVisitor("alice"), Is.EqualTo(storeRaw ? 1 : 0));
  }

  [TestCase(false)]
  [TestCase(true)]
  public async Task ErasureRemovesPersistentSessionAcrossRestart(bool storeRaw)
  {
    void Configure(AnalyticsOptions options)
    {
      options.Storage.StoreRawEvents = storeRaw;
      options.Ingestion.WorkerCount = 4;
    }
    using (var engine = TestEngine.Create(directory, Configure))
    {
      engine.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/before", Timestamp = Utc(Day0) });
      engine.Flush();
      Assert.That(await engine.EraseVisitorAsync("alice"), Is.EqualTo(storeRaw ? 1 : 0));
    }
    using var reopened = TestEngine.Create(directory, Configure);
    // This timestamp could continue the stored session if its deletion were missing.
    reopened.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/after", Timestamp = Utc(Day0.AddMinutes(1)) });
    reopened.Flush();
    var result = reopened.Query(new AnalyticsQuery
    {
      Range = Day(Day0), GroupBy = { StandardDimensions.EntryPage }, Metrics = { Metric.Sessions }
    });
    Assert.Multiple(() =>
    {
      Assert.That(reopened.GetStatistics().SessionsStarted, Is.EqualTo(1));
      Assert.That(result.Rows.Select(x => x.Dimensions[0]), Is.EquivalentTo(new[] { "/before", "/after" }));
    });
  }

  [TestCase(1)]
  [TestCase(2)]
  public void FlushFailureIsLoggedAndIngestionContinues(int failOnOpen)
  {
    var opens = 0;
    var logger = new RecordingLogger();
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.Logger = logger;
      o.Storage.Rollups.ConfigureMaintainer = _ =>
      {
        if (Interlocked.Increment(ref opens) == failOnOpen)
          throw new IOException("Rollup open failure");
      };
    });
    engine.Track(new AnalyticsEvent("before") { VisitorId = "alice", Timestamp = Utc(Day0.AddHours(1)) });
    Assert.That(Assert.Throws<AnalyticsStorageException>(() => engine.Flush()).InnerException, Is.TypeOf<IOException>());
    Assert.That(engine.Track(new AnalyticsEvent("after") { VisitorId = "bob", Timestamp = Utc(Day0.AddHours(2)) }), Is.True);
    engine.Flush();
    Assert.Multiple(() =>
    {
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(1));
      Assert.That(engine.GetStatistics().EventsProcessed, Is.EqualTo(2));
      Assert.That(logger.Problems.Any(x => x.Contains("Rollup open failure", StringComparison.Ordinal)), Is.True);
      Assert.That(engine.Query(new AnalyticsQuery { Event = "after", Range = Day(Day0), Metrics = { Metric.Events } })
          .Totals[Metric.Events], Is.EqualTo(1));
      foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups, QuerySource.Auto })
      {
        Assert.That(engine.Query(new AnalyticsQuery { Event = "before", Range = Day(Day0), Source = source, Metrics = { Metric.Events } })
            .Totals[Metric.Events], Is.EqualTo(1));
      }
    });
  }

  [TestCase(Granularity.Day)]
  [TestCase(Granularity.Minute)]
  public void AddedGranularityUsesExistingHistoricalData(Granularity added)
  {
    using (var engine = TestEngine.Create(directory, o => o.Rollups.Granularities = Granularity.Hour))
      engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(Day0.AddHours(1)) });
    using var reopened = TestEngine.Create(directory, o => o.Rollups.Granularities = Granularity.Hour | added);
    var result = reopened.Query(new AnalyticsQuery
    {
      Event = "purchase", Range = Day(Day0), Metrics = { Metric.Events },
      Interval = added == Granularity.Minute ? TimeInterval.Minute : TimeInterval.None
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
      Assert.That(result.Plan.IsComplete, Is.True);
    });
  }

  [Test]
  public void ReEnabledGranularityDoesNotClaimItsGap()
  {
    using (var engine = TestEngine.Create(directory))
      engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(Day0.AddHours(1)) });
    using (var engine = TestEngine.Create(directory, o => o.Rollups.Granularities = Granularity.Hour))
      engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(Day0.AddHours(2)) });
    using var reopened = TestEngine.Create(directory);
    var result = reopened.Query(new AnalyticsQuery { Event = "purchase", Range = Day(Day0), Metrics = { Metric.Events } });
    Assert.Multiple(() =>
    {
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(2));
      Assert.That(result.Plan.IsComplete, Is.True);
      Assert.That(result.Plan.Steps.All(x => x.Source == PlanSource.HourRollups), Is.True);
    });
  }

  [Test]
  public void RawQueriesFollowLongSessionsAndDoNotCountPrematureExits()
  {
    using var engine = TestEngine.Create(directory);
    for (var i = 0; i <= 12; ++i)
    {
      engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = "alice", Url = "/p" + i, Timestamp = Utc(Day0.AddMinutes(10 + i * 20))
      });
    }
    engine.Flush();
    foreach (var (from, to) in new[] { (Day0, Day0.AddHours(1)), (Day0.AddHours(2), Day0.AddHours(3)), (Day0, Day0.AddHours(5)) })
    {
      var query = new AnalyticsQuery
      {
        Range = Range(from, to), Metrics = { Metric.PageViews, Metric.Sessions, Metric.SessionDuration, Metric.PageViewsPerSession, Metric.Exits }
      };
      var auto = engine.Query(query);
      query.Source = QuerySource.RawEvents;
      var raw = engine.Query(query);
      Assert.That(raw.Totals.Values, Is.EqualTo(auto.Totals.Values));
    }
  }

  [Test]
  public void DisposeReleasesStorageWhenMetadataWriteFails()
  {
    var engine = TestEngine.Create(directory);
    engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(Day0.AddHours(1)) });
    var paths = new[] { Path.Combine(directory, "meta", "meta-a.bin"), Path.Combine(directory, "meta", "meta-b.bin") };
    foreach (var path in paths)
    {
      File.Move(path, path + ".bak");
      Directory.CreateDirectory(path);
    }
    try
    {
      Assert.Throws<UnauthorizedAccessException>(() => engine.Dispose());
    }
    finally
    {
      foreach (var path in paths)
      {
        Directory.Delete(path);
        File.Move(path + ".bak", path);
      }
    }
    using var reopened = TestEngine.Create(directory);
    Assert.That(reopened.Query(new AnalyticsQuery { Event = "purchase", Range = Day(Day0), Metrics = { Metric.Events } })
        .Totals[Metric.Events], Is.EqualTo(1));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void EventNamesRespectRangeAcrossDimensionSetsInTheSamePartition(bool storeRaw)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.StoreRawEvents = storeRaw;
      o.Rollups.Definitions.Clear();
      o.Rollups.Definitions.Add(new RollupDefinition("plan"));
      o.Rollups.Definitions.Add(new RollupDefinition("region"));
    });
    engine.Track(new AnalyticsEvent("old") { Timestamp = Utc(Day0.AddDays(-1)) });
    engine.Track(new AnalyticsEvent("new") { Timestamp = Utc(Day0.AddHours(1)) });
    engine.Track(new AnalyticsEvent("future") { Timestamp = Utc(Day0.AddDays(1)) });
    engine.Flush();
    Assert.That(engine.GetEventNames(Day(Day0)), Is.EqualTo(new[] { "new" }));
  }

  [Test]
  public void EventNamesReadRawEdgesAndWorkWithoutRollups()
  {
    using var engine = TestEngine.Create(directory, o => o.Rollups.Granularities = Granularity.None);
    engine.Track(new AnalyticsEvent("before") { Timestamp = Utc(Day0.AddMinutes(1)) });
    engine.Track(new AnalyticsEvent("inside") { Timestamp = Utc(Day0.AddMinutes(3)) });
    engine.Track(new AnalyticsEvent("after") { Timestamp = Utc(Day0.AddMinutes(5)) });
    engine.Flush();
    Assert.That(engine.GetEventNames(Range(Day0.AddMinutes(2), Day0.AddMinutes(4))), Is.EqualTo(new[] { "inside" }));
  }

  [TestCase(false, "/pricing")]
  [TestCase(true, "/pricing?campaign_id=1&plan=pro")]
  public void HashRoutesPreserveCampaignContextAndQueryPrivacy(bool includeQuery, string expectedPath)
  {
    var parsed = UrlParser.Parse(
        "https://example.com/?utm_source=newsletter&campaign_id=1#/pricing?utm_campaign=spring&plan=pro",
        new WebOptions(), includeQuery, hashRouting: true);
    Assert.Multiple(() =>
    {
      Assert.That(parsed.Path, Is.EqualTo(expectedPath));
      Assert.That(parsed.UtmSource, Is.EqualTo("newsletter"));
      Assert.That(parsed.UtmCampaign, Is.EqualTo("spring"));
    });
  }
}
