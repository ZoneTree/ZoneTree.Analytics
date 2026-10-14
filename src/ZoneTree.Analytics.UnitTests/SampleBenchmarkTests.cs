namespace ZoneTree.Analytics.UnitTests;

public sealed class SampleBenchmarkTests : DirectoryTest
{
  [TestCase(false)]
  [TestCase(true)]
  public void PreparationPreservesTheRealisticDataShape(bool percentiles)
  {
    var end = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    var request = new SampleBenchmarkRequest(40, 7, 4, percentiles);
    var events = SampleBenchmark.PrepareEvents(request, TimeZoneInfo.Utc, _ => { }, end);
    var expected = SampleTraffic.CreateEvents(40, 7, timeZone: TimeZoneInfo.Utc, endingAt: end).ToList();
    Assert.That(events, Has.Length.EqualTo(40));
    for (var index = 0; index < events.Length; index++)
    {
      Assert.That(events[index].Name, Is.EqualTo(expected[index].Name));
      Assert.That(events[index].Timestamp, Is.EqualTo(expected[index].Timestamp));
      Assert.That(events[index].Url, Is.EqualTo(expected[index].Url));
      Assert.That(events[index].Referrer, Is.EqualTo(expected[index].Referrer));
      Assert.That(events[index].Values, Is.EquivalentTo(expected[index].Values));
    }
  }

  [TestCase(1, false)]
  [TestCase(4, true)]
  public void BenchmarkUsesDefaultPerWorkerLimitsAndOptionalPercentiles(int workers, bool percentiles)
  {
    var options = DashboardAnalyticsOptions.CreateBenchmark(directory, TimeZoneInfo.Utc, workers, percentiles);
    var defaults = new AnalyticsOptions();
    Assert.That(options.Ingestion.WorkerCount, Is.EqualTo(workers));
    Assert.That(options.Ingestion.QueueCapacity, Is.EqualTo(defaults.Ingestion.QueueCapacity));
    Assert.That(options.Ingestion.MaxBufferedRollupRows, Is.EqualTo(defaults.Ingestion.MaxBufferedRollupRows));
    Assert.That(options.Sessions.MaxCachedSessions, Is.EqualTo(defaults.Sessions.MaxCachedSessions));
    Assert.That(options.Ingestion.QueueFullBehavior, Is.EqualTo(QueueFullBehavior.Wait));
    Assert.That(options.Rollups.ValueAggregates.ContainsKey("load_time"), Is.EqualTo(percentiles));
  }

  [Test]
  public async Task SmallFixtureFlushesAllInputsAndRetainsBrowsableReports()
  {
    Directory.CreateDirectory(directory);
    var sentinel = Path.Combine(directory, "existing-data.txt");
    await File.WriteAllTextAsync(sentinel, "preserve");
    await using var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc);
    var phases = new List<string>();
    var result = await benchmark.RunAsync(new(17, 7, 2), update => phases.Add(update.State),
        CancellationToken.None, CancellationToken.None);
    Assert.That(result, Is.Not.Null);
    Assert.That(result.Ingested, Is.EqualTo(17));
    Assert.That(result.Errors, Is.Zero);
    Assert.That(result.Dropped, Is.Zero);
    Assert.That(result.Rejected, Is.Zero);
    Assert.That(result.IngestionMs, Is.GreaterThanOrEqualTo(result.SubmissionMs));
    Assert.That(phases, Does.Contain("preparing").And.Contain("running").And.Contain("flushing").And.Contain("compacting"));
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "data/ingestion-benchmarks")), Has.Length.EqualTo(1));
    using (var lease = benchmark.Reports.Acquire())
    {
      var report = lease.Engine.Web.GetSummary(new() { Site = BenchmarkReportStore.Site, Range = DateRange.LastDays(8) });
      Assert.That(report.Summary.PageViews, Is.GreaterThan(0));
      Assert.That(lease.Engine.RollupIndexes.AllStores.Count(), Is.EqualTo(2));
      foreach (var index in lease.Engine.RollupIndexes.AllStores)
        foreach (var partition in index.ListPartitions())
        {
          using var indexLease = index.Acquire(partition);
          Assert.That(indexLease.Tree.Maintenance.MutableSegmentRecordCount, Is.Zero);
          Assert.That(indexLease.Tree.Maintenance.ReadOnlySegmentsRecordCount, Is.Zero);
        }
    }
    await benchmark.Reports.ClearAsync(CancellationToken.None);
    Assert.That(benchmark.Reports.Result, Is.Null);
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "data/ingestion-benchmarks")), Is.Empty);
    Assert.That(await File.ReadAllTextAsync(sentinel), Is.EqualTo("preserve"));
  }

  [Test]
  public async Task CancellationDrainsAndCleansTheTemporaryStore()
  {
    await using var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc);
    using var cancellation = new CancellationTokenSource();
    var result = await benchmark.RunAsync(new(25, 1, 1), update =>
    {
      if (update.State is "ingesting" or "flushing") cancellation.Cancel();
    }, cancellation.Token, CancellationToken.None);
    Assert.That(result, Is.Null);
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "data/ingestion-benchmarks")), Is.Empty);
  }

  [Test]
  public async Task SuccessfulRunsReplaceTheRetainedDatasetAndSurviveRestart()
  {
    await using (var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc))
    {
      await benchmark.RunAsync(new(17, 7, 2), _ => { }, CancellationToken.None, CancellationToken.None);
      var updates = new List<SampleBenchmarkProgress>();
      await benchmark.RunAsync(new(25, 7, 2), updates.Add, CancellationToken.None, CancellationToken.None);
      Assert.That(updates.Any(update => update.Ingested == 25), Is.True);
      Assert.That(updates.All(update => update.Ingested <= update.Submitted), Is.True);
      Assert.That(Directory.GetDirectories(Path.Combine(directory, "data/ingestion-benchmarks")), Has.Length.EqualTo(1));
    }
    await using var reopened = new SampleBenchmark(directory, TimeZoneInfo.Utc);
    Assert.That(reopened.Reports.Result.Ingested, Is.EqualTo(25));
    using var lease = reopened.Reports.Acquire();
    Assert.That(lease.Engine.Web.GetSummary(new() { Site = BenchmarkReportStore.Site }).Summary.PageViews, Is.GreaterThan(0));
  }

  [Test]
  public async Task PublishingWaitsForPreviousReportReaders()
  {
    await using var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc);
    await benchmark.RunAsync(new(17, 7, 2), _ => { }, CancellationToken.None, CancellationToken.None);
    using var previous = benchmark.Reports.Acquire();
    var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var next = benchmark.RunAsync(new(25, 7, 2), update =>
    {
      if (update.State == "publishing") publishing.TrySetResult();
    }, CancellationToken.None, CancellationToken.None);
    await publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.That(next.IsCompleted, Is.False);
    Assert.That(previous.Engine.Web.GetSummary(new() { Site = BenchmarkReportStore.Site }).Summary.PageViews, Is.GreaterThan(0));
    previous.Dispose();
    Assert.That((await next).Ingested, Is.EqualTo(25));
  }
}
