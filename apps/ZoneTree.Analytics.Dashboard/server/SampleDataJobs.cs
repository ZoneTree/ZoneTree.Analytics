using System.Diagnostics;
using ZoneTree.Analytics;

sealed record SampleGenerationRequest(int Events, int Days = 60, int Workers = 1);
sealed record SampleDataStatus(string State, int Requested, int Processed, long TotalGenerated, long ElapsedMs, string Error,
    int Workers = 1, long Ingested = 0, double EventsPerSecond = 0, double AverageEventsPerSecond = 0,
    string ActiveOperation = null, SampleBenchmarkStatus Benchmark = null);

sealed class SampleDataJobs(SampleDataStore store, SampleBenchmark benchmark, IHostApplicationLifetime lifetime, ILogger<SampleDataJobs> logger)
    : IHostedService, IDisposable
{
  readonly object sync = new();
  SampleDataStatus status = new("idle", 0, 0, store.ReadCount(), 0, null);
  CancellationTokenSource cancellation;
  Task operation = Task.CompletedTask;
  string activeOperation;
  SampleBenchmarkStatus benchmarkStatus = new(Result: benchmark.Reports.Result);

  public SampleDataStatus Status
  {
    get
    {
      lock (sync) return status with
      {
        ActiveOperation = operation.IsCompleted ? null : activeOperation,
        Benchmark = benchmarkStatus with
        {
          GeolocationAvailable = benchmark.AvailableGeoPrecision != GeoPrecision.None,
          CitiesAvailable = benchmark.AvailableGeoPrecision == GeoPrecision.City
        }
      };
    }
  }
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  public bool Generate(SampleGenerationRequest request)
  {
    if (request.Events is < 1 or > 10_000_000 || request.Days is < 1 or > 365 || request.Workers is < 1 or > 64)
      throw new ArgumentException("Choose 1–10,000,000 events, 365 days, and 1–64 workers.");
    request = request with { Workers = Math.Min(request.Workers, request.Events) };
    lock (sync)
    {
      if (!operation.IsCompleted) return false;
      cancellation?.Dispose();
      cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
      activeOperation = "generation";
      status = status with
      {
        State = "generating",
        Requested = request.Events,
        Processed = 0,
        ElapsedMs = 0,
        Error = null,
        Workers = request.Workers,
        Ingested = 0,
        EventsPerSecond = 0,
        AverageEventsPerSecond = 0
      };
      var token = cancellation.Token;
      operation = Task.Run(() => GenerateAsync(request, token));
      return true;
    }
  }

  public bool Benchmark(SampleBenchmarkRequest request)
  {
    if (request.Events is < 1 or > 10_000_000 || request.Days is < 1 or > 365 || request.Workers is < 1 or > 64)
      throw new ArgumentException("Choose 1–10,000,000 events, 365 days, and 1–64 workers.");
    if (request.Cities && !request.Geolocation)
      throw new ArgumentException("Resolving cities needs geolocation.");
    if (request.Geolocation && benchmark.AvailableGeoPrecision == GeoPrecision.None)
      throw new ArgumentException("Geolocation needs an IP location database: choose one in the Location settings and wait for its download.");
    if (request.Cities && benchmark.AvailableGeoPrecision != GeoPrecision.City)
      throw new ArgumentException("Resolving cities needs a database with cities: choose DB-IP Lite in the Location settings.");
    lock (sync)
    {
      if (!operation.IsCompleted) return false;
      cancellation?.Dispose();
      cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
      var token = cancellation.Token;
      activeOperation = "benchmark";
      benchmarkStatus = new("preparing", request.Events, Workers: request.Workers, Days: request.Days,
          Percentiles: request.Percentiles, Indexes: request.Indexes, Geolocation: request.Geolocation, Cities: request.Cities, Result: benchmarkStatus.Result);
      operation = Task.Run(async () =>
      {
        try
        {
          var result = await benchmark.RunAsync(request, update =>
          {
            lock (sync) benchmarkStatus = benchmarkStatus with
            {
              State = update.State,
              Prepared = Math.Max(benchmarkStatus.Prepared, update.Prepared),
              Submitted = Math.Max(benchmarkStatus.Submitted, update.Submitted),
              Ingested = Math.Max(benchmarkStatus.Ingested, update.Ingested),
              ElapsedMs = update.ElapsedMs,
              EventsPerSecond = update.EventsPerSecond,
            };
          }, token, lifetime.ApplicationStopping);
          lock (sync) benchmarkStatus = benchmarkStatus with
          { State = result == null ? "cancelled" : "completed", Result = result ?? benchmarkStatus.Result };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
          lock (sync) benchmarkStatus = benchmarkStatus with { State = "cancelled" };
        }
        catch (Exception error)
        {
          logger.LogError(error, "Ingestion benchmark failed.");
          lock (sync) benchmarkStatus = benchmarkStatus with { State = "failed", Error = error.Message };
        }
      });
      return true;
    }
  }

  public bool ClearBenchmark()
  {
    lock (sync)
    {
      if (!operation.IsCompleted) return false;
      activeOperation = "benchmark-deletion";
      operation = Task.Run(async () =>
      {
        try
        {
          await benchmark.Reports.ClearAsync(lifetime.ApplicationStopping);
          lock (sync) benchmarkStatus = new();
        }
        catch (Exception error)
        {
          logger.LogError(error, "Benchmark data deletion failed.");
          lock (sync) benchmarkStatus = benchmarkStatus with { Error = error.Message };
        }
      });
      return true;
    }
  }

  async Task GenerateAsync(SampleGenerationRequest request, CancellationToken token)
  {
    var timer = new Stopwatch();
    var total = Status.TotalGenerated;
    var processed = 0;
    try
    {
      await store.PrepareGenerationAsync(request.Workers, token);
      using var lease = store.Acquire();
      var baseline = lease.Engine.GetStatistics();
      timer.Start();
      using var monitoring = new CancellationTokenSource();
      var monitor = MonitorAsync(lease.Engine, baseline, timer, monitoring.Token);
      try
      {
        try
        {
          await SampleTraffic.GenerateAsync(lease.Engine, request.Events, request.Days, SampleDataStore.Site,
              count => { processed = count; lock (sync) status = status with { Processed = Math.Max(status.Processed, count) }; }, request.Workers, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
          // Drain accepted events before publishing the final cancelled status.
          await lease.Engine.FlushAsync(lifetime.ApplicationStopping);
        }
      }
      finally
      {
        monitoring.Cancel();
        await monitor;
        timer.Stop();
        Snapshot(lease.Engine.GetStatistics(), baseline, timer, 0, 0);
      }
      if (Status.Ingested != processed)
        throw new InvalidOperationException("Some generated events could not be ingested.");
      if (!token.IsCancellationRequested)
      {
        lock (sync) status = status with { State = "compacting" };
        await lease.Engine.CompactAsync(lifetime.ApplicationStopping);
      }
      await store.WriteCountAsync(total + processed);
      lock (sync) status = status with { State = token.IsCancellationRequested ? "cancelled" : "completed", TotalGenerated = total + processed };
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
      if (processed > 0) await store.WriteCountAsync(total + processed);
      lock (sync) status = status with { State = "cancelled", TotalGenerated = total + processed };
    }
    catch (Exception error)
    {
      logger.LogError(error, "Sample data generation failed.");
      if (processed > 0) await store.WriteCountAsync(total + processed);
      lock (sync) status = status with { State = "failed", Error = error.Message, TotalGenerated = total + processed };
    }
    finally { lock (sync) status = status with { ElapsedMs = timer.ElapsedMilliseconds }; }
  }

  async Task MonitorAsync(AnalyticsEngine engine, AnalyticsStatistics baseline, Stopwatch timer, CancellationToken token)
  {
    using var interval = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
    var previous = 0L;
    var previousSeconds = 0.0;
    try
    {
      while (await interval.WaitForNextTickAsync(token))
      {
        var statistics = engine.GetStatistics();
        Snapshot(statistics, baseline, timer, previous, previousSeconds);
        previous = statistics.EventsProcessed - baseline.EventsProcessed;
        previousSeconds = timer.Elapsed.TotalSeconds;
      }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
  }

  void Snapshot(AnalyticsStatistics statistics, AnalyticsStatistics baseline, Stopwatch timer, long previous, double previousSeconds)
  {
    var ingested = statistics.EventsProcessed - baseline.EventsProcessed;
    var seconds = timer.Elapsed.TotalSeconds;
    lock (sync) status = status with
    {
      Processed = Math.Max(status.Processed, (int)(statistics.EventsAccepted - baseline.EventsAccepted)),
      Ingested = ingested,
      ElapsedMs = timer.ElapsedMilliseconds,
      EventsPerSecond = (ingested - previous) / Math.Max(0.001, seconds - previousSeconds),
      AverageEventsPerSecond = ingested / Math.Max(0.001, seconds),
    };
  }

  public bool Clear()
  {
    lock (sync)
    {
      if (!operation.IsCompleted) return false;
      status = status with { State = "deleting", Error = null };
      activeOperation = "deletion";
      operation = Task.Run(async () =>
      {
        try
        {
          await store.ClearAsync(lifetime.ApplicationStopping);
          lock (sync) status = new("idle", 0, 0, 0, 0, null);
        }
        catch (Exception error)
        {
          logger.LogError(error, "Sample data deletion failed.");
          lock (sync) status = status with { State = "failed", Error = error.Message };
        }
      });
      return true;
    }
  }

  public void Cancel() { lock (sync) cancellation?.Cancel(); }
  public async Task StopAsync(CancellationToken cancellationToken)
  {
    Cancel();
    await operation.WaitAsync(cancellationToken);
  }
  public void Dispose() => cancellation?.Dispose();
}
