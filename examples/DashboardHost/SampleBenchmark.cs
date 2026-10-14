#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Microsoft.Extensions.Logging;
using ZoneTree.Analytics;
using ZoneTree.Analytics.Web;

sealed record SampleBenchmarkRequest(int Events = 5_000_000, int Days = 90, int Workers = 20, bool Percentiles = false);
sealed record SampleBenchmarkResult(int Events, int Days, int Workers, bool Percentiles,
    double PreparationMs, double SubmissionMs, double IngestionMs, double EventsPerSecond,
    long Ingested, long Errors, long Dropped, long Rejected, string Build, string GarbageCollection, string TieredCompilation,
    string FromDate = "", string ToDate = "");
sealed record SampleBenchmarkStatus(string State = "idle", int Requested = 0, int Prepared = 0, int Submitted = 0,
    int Workers = 20, int Days = 90, bool Percentiles = false, string? Error = null, SampleBenchmarkResult? Result = null,
    long Ingested = 0, double ElapsedMs = 0, double EventsPerSecond = 0);
sealed record SampleBenchmarkProgress(string State, int Prepared = 0, int Submitted = 0,
    long Ingested = 0, double ElapsedMs = 0, double EventsPerSecond = 0);

// Preparation uses the same realistic generator. The measured interval contains
// submission and the final flush; store creation, statistics and cleanup are outside it.
sealed class SampleBenchmark(string contentRoot, TimeZoneInfo timeZone, ILogger? logger = null) : IAsyncDisposable
{
  public BenchmarkReportStore Reports { get; } = new(contentRoot, logger);
  public static AnalyticsOptions CreateOptions(string directory, int workers, bool percentiles)
  {
    var options = new AnalyticsOptions
    {
      DataDirectory = directory,
      DefaultSite = BenchmarkReportStore.Site,
      Ingestion = { WorkerCount = workers, QueueFullBehavior = QueueFullBehavior.Wait },
      Web = { Bots = BotTraffic.TrackSeparately, GeoLocator = new SampleBenchmarkGeoLocator() },
    };
    if (percentiles) options.Rollups.ValueAggregates["load_time"] = ValueAggregates.Percentiles;
    return options;
  }

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

  public async Task<SampleBenchmarkResult?> RunAsync(SampleBenchmarkRequest request, Action<SampleBenchmarkProgress> progress,
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

  async Task<SampleBenchmarkResult?> MeasureAsync(string directory, AnalyticsEvent[] events, SampleBenchmarkRequest request, DateTimeOffset end,
      double preparationMs, Action<SampleBenchmarkProgress> progress, CancellationToken cancellationToken, CancellationToken applicationStopping)
  {
    var options = CreateOptions(directory, request.Workers, request.Percentiles);
    options.TimeZone = timeZone;
    await using var engine = new AnalyticsEngine(options);
    progress(new("running", Prepared: events.Length));
    var stopwatch = Stopwatch.StartNew();
    using var monitoring = new CancellationTokenSource();
    var monitor = MonitorAsync(engine, events.Length, stopwatch, progress, cancellationToken, monitoring.Token);
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
      PublishProgress(engine, events.Length, stopwatch, progress, cancellationToken.IsCancellationRequested);
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

sealed class SampleBenchmarkGeoLocator : IGeoLocator
{
  public GeoLocation Locate(string ipAddress) => new(ipAddress.EndsWith(".1", StringComparison.Ordinal) ? "US" : "DE");
}
