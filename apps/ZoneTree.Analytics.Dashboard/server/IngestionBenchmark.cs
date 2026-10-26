using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Microsoft.Extensions.Logging;
using ZoneTree.Analytics;

sealed record BenchmarkRequest(int Events = 5_000_000, int Days = 365, int Workers = 20, bool Percentiles = false,
    bool Indexes = true, bool Geolocation = false, bool Cities = false);
sealed record BenchmarkResult(int Events, int Days, int Workers, bool Percentiles,
    double PreparationMs, double SubmissionMs, double IngestionMs, double EventsPerSecond,
    long Ingested, long Errors, long Dropped, long Rejected, string Build, string GarbageCollection, string TieredCompilation,
    string FromDate = "", string ToDate = "", bool Indexes = true, bool Geolocation = false, bool Cities = false);
sealed record BenchmarkStatus(string State = "idle", int Requested = 0, int Prepared = 0, int Submitted = 0,
    int Workers = 20, int Days = 365, bool Percentiles = false, bool Indexes = true, string Error = null, BenchmarkResult Result = null,
    long Ingested = 0, double ElapsedMs = 0, double EventsPerSecond = 0, bool Geolocation = false,
    bool Cities = false, bool GeolocationAvailable = false, bool CitiesAvailable = false);
sealed record BenchmarkProgress(string State, int Prepared = 0, int Submitted = 0,
    long Ingested = 0, double ElapsedMs = 0, double EventsPerSecond = 0);

// Preparation generates realistic traffic in memory. The measured interval contains
// submission and the final flush; store creation, statistics and cleanup are outside it.
// Each run takes the reporting time zone current when it starts. With geolocation, visitors carry
// addresses the engine resolves to their countries, and with cities also to regions and cities,
// instead of country dimensions, so the kinds of run differ only by the lookups. A run loads the
// database files of the IP location provider current when it starts, to the precision it measures.
sealed class IngestionBenchmark(string contentRoot, Func<TimeZoneInfo> currentTimeZone, ILogger logger = null,
    Func<IpLocationProvider> currentIpLocation = null) : IAsyncDisposable
{
  public IngestionBenchmark(string contentRoot, TimeZoneInfo timeZone, ILogger logger = null, IpLocationProvider ipLocation = null)
      : this(contentRoot, () => timeZone, logger, () => ipLocation) { }

  public BenchmarkReportStore Reports { get; } = new(contentRoot, logger);

  // The most precise location a run can resolve: none without a database.
  public GeoPrecision AvailableGeoPrecision =>
      currentIpLocation?.Invoke()?.Database?.AvailablePrecision ?? GeoPrecision.None;

  public static AnalyticsEvent[] PrepareEvents(BenchmarkRequest request, TimeZoneInfo timeZone,
      Action<int> progress, DateTimeOffset? endingAt = null, CancellationToken cancellationToken = default,
      SampleAddresses addresses = null)
  {
    var events = new AnalyticsEvent[request.Events];
    var index = 0;
    foreach (var item in SampleTraffic.CreateEvents(request.Events, request.Days, timeZone: timeZone, endingAt: endingAt,
        addresses: addresses))
    {
      cancellationToken.ThrowIfCancellationRequested();
      events[index++] = item;
      if (index % 1000 == 0) progress(index);
    }
    progress(index);
    return events;
  }

  public async Task<BenchmarkResult> RunAsync(BenchmarkRequest request, Action<BenchmarkProgress> progress,
      CancellationToken cancellationToken, CancellationToken applicationStopping)
  {
    var timeZone = currentTimeZone();
    var preparation = Stopwatch.StartNew();
    var end = DateTimeOffset.UtcNow.AddSeconds(-1);
    var precision = !request.Geolocation ? GeoPrecision.None : request.Cities ? GeoPrecision.City : GeoPrecision.Country;
    IpLocationDatabase geoLocator = null;
    if (precision != GeoPrecision.None)
    {
      var ipLocation = currentIpLocation?.Invoke();
      if ((ipLocation?.Database?.AvailablePrecision ?? GeoPrecision.None) < precision)
        throw new InvalidOperationException(request.Cities
            ? "Resolving cities needs an IP location database with cities."
            : "Geolocation needs an IP location database.");
      // Loaded to the measured precision: a country run does not search city ranges.
      geoLocator = IpLocationDatabase.Load(ipLocation.Paths, precision);
    }
    var addresses = geoLocator == null ? null : SampleTraffic.FindAddresses(geoLocator);
    var events = PrepareEvents(request, timeZone, count => progress(new("preparing", Prepared: count)), end, cancellationToken,
        addresses);
    preparation.Stop();
    var directory = Reports.CreateRunDirectory();
    try
    {
      var result = await MeasureAsync(directory, events, request, geoLocator, precision, end, timeZone, preparation.Elapsed.TotalMilliseconds,
          progress, cancellationToken, applicationStopping);
      if (result == null) return null;
      progress(new("publishing", events.Length, events.Length, result.Ingested, result.IngestionMs, result.EventsPerSecond));
      await Reports.PublishAsync(directory, timeZone.Id, result, cancellationToken);
      return result;
    }
    finally { Reports.RemoveUnpublishedRun(directory); }
  }

  async Task<BenchmarkResult> MeasureAsync(string directory, AnalyticsEvent[] events, BenchmarkRequest request,
      IGeoLocator geoLocator, GeoPrecision geoPrecision, DateTimeOffset end,
      TimeZoneInfo timeZone, double preparationMs, Action<BenchmarkProgress> progress, CancellationToken cancellationToken, CancellationToken applicationStopping)
  {
    var options = DashboardAnalyticsOptions.CreateBenchmark(directory, timeZone, request.Workers, request.Percentiles, request.Indexes, logger,
        geoLocator, geoPrecision);
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
          TimeZoneInfo.ConvertTime(end, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), request.Indexes,
          request.Geolocation, request.Geolocation && request.Cities);
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
      Action<BenchmarkProgress> progress, CancellationToken cancellationToken, CancellationToken stop)
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
      Action<BenchmarkProgress> progress, bool cancelling)
  {
    var statistics = engine.GetStatistics(includePartitions: false);
    var phase = cancelling ? "cancelling" : statistics.EventsProcessed >= requested ? "flushing" : "ingesting";
    progress(new(phase, requested, (int)Math.Max(statistics.EventsAccepted, statistics.EventsProcessed), statistics.EventsProcessed,
        stopwatch.Elapsed.TotalMilliseconds, statistics.EventsProcessed / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)));
  }

  public ValueTask DisposeAsync() => Reports.DisposeAsync();
}
