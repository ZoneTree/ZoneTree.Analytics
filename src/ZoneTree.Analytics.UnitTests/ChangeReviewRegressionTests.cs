using ZoneTree.Analytics.Storage;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class ChangeReviewRegressionTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-2);

  static void RemoveTierMetadata(string root)
  {
    new MetaStore(Path.Combine(root, "meta")).Update(entries =>
    {
      foreach (var key in entries.Keys.Where(x => x.StartsWith("granularity:", StringComparison.Ordinal)).ToArray())
        entries.Remove(key);
      entries.Remove("granularity-format");
    });
  }

  [Test]
  public void LegacyDailyHistorySurvivesWhenFinerDataExpired()
  {
    var oldDay = DateTime.UtcNow.Date.AddDays(-500);
    void Configure(AnalyticsOptions options)
    {
      options.Retention.RawEvents = TimeSpan.FromDays(90);
      options.Retention.HourRollups = TimeSpan.FromDays(400);
    }
    using (var engine = TestEngine.Create(directory, Configure))
    {
      engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(oldDay.AddHours(1)) });
      engine.Flush();
    }
    RemoveTierMetadata(directory);
    // Check both the migration and subsequent reopen, with no raw/hourly data to fall back to.
    for (var run = 0; run < 2; ++run)
    {
      using var reopened = TestEngine.Create(directory, Configure);
      foreach (var source in new[] { QuerySource.Auto, QuerySource.Rollups })
      {
        var result = reopened.Query(new AnalyticsQuery
        {
          Event = "purchase", Range = Day(oldDay), Source = source, Metrics = { Metric.Events }
        });
        Assert.Multiple(() =>
        {
          Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
          Assert.That(result.Plan.IsComplete, Is.True);
        });
      }
      Assert.That(reopened.GetEventNames(Day(oldDay)), Does.Contain("purchase"));
    }
  }

  [Test]
  public void LegacyMigrationDoesNotInventCoverageForANewTier()
  {
    using (var engine = TestEngine.Create(directory, o => o.Rollups.Granularities = Granularity.Hour))
      engine.Track(new AnalyticsEvent("purchase") { Timestamp = Utc(Day0.AddHours(1)) });
    RemoveTierMetadata(directory);
    using var reopened = TestEngine.Create(directory);
    var result = reopened.Query(new AnalyticsQuery { Event = "purchase", Range = Day(Day0), Metrics = { Metric.Events } });
    Assert.Multiple(() =>
    {
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
      Assert.That(result.Plan.Steps.All(x => x.Source == PlanSource.HourRollups), Is.True);
      Assert.That(result.Plan.IsComplete, Is.True);
    });
  }

  [Test]
  public void LegacyDefaultSiteIdentityAndErasureSurviveReopen()
  {
    void Configure(AnalyticsOptions options)
    {
      options.DefaultSite = " shop ";
      options.Ingestion.WorkerCount = 4;
    }
    using (var engine = TestEngine.Create(directory, Configure))
      engine.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/before", Timestamp = Utc(Day0) });
    using var reopened = TestEngine.Create(directory, Configure);
    Assert.Multiple(() =>
    {
      Assert.That(reopened.DefaultSite, Is.EqualTo(" shop "));
      Assert.That(reopened.Query(new AnalyticsQuery { Range = Day(Day0), Metrics = { Metric.PageViews } })
          .Totals[Metric.PageViews], Is.EqualTo(1));
    });
    Assert.That(reopened.EraseVisitor("alice"), Is.EqualTo(1));
    reopened.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/after", Timestamp = Utc(Day0.AddMinutes(1)) });
    reopened.Flush();
    Assert.That(reopened.GetStatistics().SessionsStarted, Is.EqualTo(1));
  }

  [TestCase(Granularity.Hour)]
  [TestCase(Granularity.Hour | Granularity.Day)]
  public void GoalsRemainVisibleInUnalignedRollupOnlyRange(Granularity granularities)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.StoreRawEvents = false;
      o.Rollups.Granularities = granularities;
    });
    engine.Track(new AnalyticsEvent("signup") { VisitorId = "alice", Timestamp = Utc(Day0.AddHours(1).AddMinutes(10)) });
    engine.Flush();
    var range = Range(Day0.AddHours(1), Day0.AddHours(1).AddMinutes(20));
    var direct = engine.Query(new AnalyticsQuery { Event = "signup", Range = range, Metrics = { Metric.Events } });
    var goals = engine.Web.GetGoals(new WebReportRequest { Range = range });
    Assert.Multiple(() =>
    {
      Assert.That(goals.Rows.Single().Name, Is.EqualTo("signup"));
      Assert.That(goals.Rows.Single().Events, Is.EqualTo((long)direct.Totals[Metric.Events]));
      Assert.That(goals.Plan.IsComplete, Is.False, "whole-bucket fallback must remain visible");
    });
  }

  [Test]
  public async Task BackgroundFlushFailureDoesNotStopIngestion()
  {
    var opens = 0;
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.StoreRawEvents = false;
      o.Sessions.Enabled = false;
      o.Rollups.Granularities = Granularity.Hour;
      o.Rollups.Definitions.Clear();
      o.Rollups.Definitions.Add(new RollupDefinition());
      o.Ingestion.MaxBufferedRollupRows = 2;
      o.Ingestion.QueueCapacity = 8;
      o.Storage.Logger = new RecordingLogger();
      o.Storage.Rollups.ConfigureMaintainer = _ =>
      {
        if (Interlocked.Increment(ref opens) == 1)
          throw new IOException("Rollup open failure");
      };
    });
    for (var i = 0; i < 100; ++i)
      Assert.That(engine.Track(new AnalyticsEvent("purchase") { Site = "s" + i, Timestamp = Utc(Day0.AddHours(1)) }), Is.True);
    await engine.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Multiple(() =>
    {
      Assert.That(engine.GetStatistics().EventsProcessed, Is.EqualTo(100));
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(1));
      Assert.That(engine.GetStatistics().QueueLength, Is.Zero);
    });
    Assert.That(engine.Track(new AnalyticsEvent("after") { Timestamp = Utc(Day0.AddHours(1)) }), Is.True);
    await engine.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Assert.That(engine.Query(new AnalyticsQuery { Event = "after", Range = Day(Day0), Metrics = { Metric.Events } })
        .Totals[Metric.Events], Is.EqualTo(1));
  }
}
