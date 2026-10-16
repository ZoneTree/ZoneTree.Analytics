using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Microsoft.Extensions.Logging;
using ZoneTree.Analytics;

sealed record SampleBenchmarkRequest(int Events = 5_000_000, int Days = 365, int Workers = 20, bool Percentiles = false);
sealed record SampleBenchmarkResult(int Events, int Days, int Workers, bool Percentiles,
    double PreparationMs, double SubmissionMs, double IngestionMs, double EventsPerSecond,
    long Ingested, long Errors, long Dropped, long Rejected, string Build, string GarbageCollection, string TieredCompilation,
    string FromDate = "", string ToDate = "");
sealed record SampleBenchmarkStatus(string State = "idle", int Requested = 0, int Prepared = 0, int Submitted = 0,
    int Workers = 20, int Days = 365, bool Percentiles = false, string Error = null, SampleBenchmarkResult Result = null,
    long Ingested = 0, double ElapsedMs = 0, double EventsPerSecond = 0);
sealed record SampleBenchmarkProgress(string State, int Prepared = 0, int Submitted = 0,
    long Ingested = 0, double ElapsedMs = 0, double EventsPerSecond = 0);

// Preparation uses the same realistic generator. The measured interval contains
// submission and the final flush; store creation, statistics and cleanup are outside it.
sealed class SampleBenchmark(string contentRoot, TimeZoneInfo timeZone, ILogger logger = null) : IAsyncDisposable
{
  public BenchmarkReportStore Reports { get; } = new(contentRoot, logger);

  public static AnalyticsEvent[] PrepareEvents(SampleBenchmarkRequest request, TimeZoneInfo timeZone,
      Action<int> progress, DateTimeOffset? endingAt = null, CancellationToken cancellationToken = default)
  {
    var events = new AnalyticsEvent[request.Events];
    var index = 0;
    foreach (var item in SampleTraffic.CreateEvents(request.Events, request.Days, timeZone: timeZone, endingAt: endingAt))
    {
      cancellationToken.ThrowIfCancellationRequested();
      events[index++] = item;
      if (index % 1000 == 0) progress(index);
    }
    progress(index);
    return events;
  }

  public async Task<SampleBenchmarkResult> RunAsync(SampleBenchmarkRequest request, Action<SampleBenchmarkProgress> progress,
      CancellationToken cancellationToken, CancellationToken applicationStopping)
  {
    var preparation = Stopwatch.StartNew();
    var end = DateTimeOffset.UtcNow.AddSeconds(-1);
    var events = PrepareEvents(request, timeZone, count => progress(new("preparing", Prepared: count)), end, cancellationToken);
    preparation.Stop();
    var directory = Reports.CreateRunDirectory();
    try
    {
      var result = await MeasureAsync(directory, events, request, end, preparation.Elapsed.TotalMilliseconds,
          progress, cancellationToken, applicationStopping);
      if (result == null) return null;
      progress(new("publishing", events.Length, events.Length, result.Ingested, result.IngestionMs, result.EventsPerSecond));
      await Reports.PublishAsync(directory, timeZone.Id, result, cancellationToken);
      return result;
    }
    finally { Reports.RemoveUnpublishedRun(directory); }
  }

  async Task<SampleBenchmarkResult> MeasureAsync(string directory, AnalyticsEvent[] events, SampleBenchmarkRequest request, DateTimeOffset end,
      double preparationMs, Action<SampleBenchmarkProgress> progress, CancellationToken cancellationToken, CancellationToken applicationStopping)
  {
    var options = DashboardAnalyticsOptions.CreateBenchmark(directory, timeZone, request.Workers, request.Percentiles, logger);
    await using var engine = new AnalyticsEngine(options);
    progress(new("running", Prepared: events.Length));
    var stopwatch = Stopwatch.StartNew();
    using var monitoring = new CancellationTokenSource();
    var monitor = MonitorAsync(engine, events.Length, stopwatch, progress, cancellationToken, monitoring.Token);
    var compacting = false;
    try
    {
      var submitted = 0;
      foreach (var item in events)
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (!engine.Track(item)) throw new InvalidOperationException("A benchmark event was not accepted.");
        submitted++;
      }
      var submissionMs = stopwatch.Elapsed.TotalMilliseconds;
      PublishProgress(engine, events.Length, stopwatch, progress, cancellationToken.IsCancellationRequested);
      await engine.FlushAsync(cancellationToken);
      stopwatch.Stop();
      var statistics = engine.GetStatistics(includePartitions: false);
      if (statistics.EventsProcessed != request.Events || statistics.Errors > 0)
        throw new InvalidOperationException("The benchmark did not ingest every event successfully.");
      // Finalize disk storage outside the measured ingestion interval before publishing reports.
      monitoring.Cancel();
      await monitor;
      PublishProgress(engine, events.Length, stopwatch, progress, cancellationToken.IsCancellationRequested);
      compacting = true;
      progress(new("compacting", events.Length, events.Length, statistics.EventsProcessed,
          stopwatch.Elapsed.TotalMilliseconds, statistics.EventsProcessed / Math.Max(0.000001, stopwatch.Elapsed.TotalSeconds)));
      // Reports measure queries that use the indexes.
      await engine.WaitForIndexesAsync(applicationStopping);
      await engine.CompactAsync(applicationStopping);
      if (engine.GetStatistics(includePartitions: false).Errors > 0)
        throw new InvalidOperationException("The benchmark dataset could not be finalized successfully.");
#if DEBUG
      const string build = "Debug";
#else
      const string build = "Release";
#endif
      var tiered = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")
          ?? Environment.GetEnvironmentVariable("COMPlus_TieredCompilation");
      return new(request.Events, request.Days, request.Workers, request.Percentiles,
          preparationMs, submissionMs, stopwatch.Elapsed.TotalMilliseconds,
          statistics.EventsProcessed / Math.Max(0.000001, stopwatch.Elapsed.TotalSeconds),
          statistics.EventsProcessed, statistics.Errors, statistics.EventsDropped, statistics.EventsRejected,
          build, GCSettings.IsServerGC ? "Server" : "Workstation", tiered == "0" ? "Disabled" : tiered == "1" ? "Enabled" : "Default",
          TimeZoneInfo.ConvertTime(end.AddDays(-request.Days), timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
          TimeZoneInfo.ConvertTime(end, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      PublishProgress(engine, events.Length, stopwatch, progress, cancelling: true);
      await engine.FlushAsync(applicationStopping);
      return null;
    }
    finally
    {
      monitoring.Cancel();
      await monitor;
      stopwatch.Stop();
      if (!compacting) PublishProgress(engine, events.Length, stopwatch, progress, cancellationToken.IsCancellationRequested);
    }
  }

  static async Task MonitorAsync(AnalyticsEngine engine, int requested, Stopwatch stopwatch,
      Action<SampleBenchmarkProgress> progress, CancellationToken cancellationToken, CancellationToken stop)
  {
    using var interval = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    try
    {
      while (await interval.WaitForNextTickAsync(stop))
        PublishProgress(engine, requested, stopwatch, progress, cancellationToken.IsCancellationRequested);
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
  }

  static void PublishProgress(AnalyticsEngine engine, int requested, Stopwatch stopwatch,
      Action<SampleBenchmarkProgress> progress, bool cancelling)
  {
    var statistics = engine.GetStatistics(includePartitions: false);
    var phase = cancelling ? "cancelling" : statistics.EventsProcessed >= requested ? "flushing" : "ingesting";
    progress(new(phase, requested, (int)Math.Max(statistics.EventsAccepted, statistics.EventsProcessed), statistics.EventsProcessed,
        stopwatch.Elapsed.TotalMilliseconds, statistics.EventsProcessed / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)));
  }

  public ValueTask DisposeAsync() => Reports.DisposeAsync();
}
