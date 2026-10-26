using ZoneTree.Analytics;

sealed record BenchmarkJobsStatus(string ActiveOperation = null, BenchmarkStatus Benchmark = null);

// Runs one benchmark operation at a time: a run or the deletion of the retained dataset.
sealed class BenchmarkJobs(IngestionBenchmark benchmark, IHostApplicationLifetime lifetime, ILogger<BenchmarkJobs> logger)
    : IHostedService, IDisposable
{
  readonly object sync = new();
  CancellationTokenSource cancellation;
  Task operation = Task.CompletedTask;
  string activeOperation;
  BenchmarkStatus benchmarkStatus = new(Result: benchmark.Reports.Result);

  public BenchmarkJobsStatus Status
  {
    get
    {
      lock (sync) return new(operation.IsCompleted ? null : activeOperation, benchmarkStatus with
      {
        GeolocationAvailable = benchmark.AvailableGeoPrecision != GeoPrecision.None,
        CitiesAvailable = benchmark.AvailableGeoPrecision == GeoPrecision.City
      });
    }
  }
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  public bool Run(BenchmarkRequest request)
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
      activeOperation = "run";
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

  public bool Clear()
  {
    lock (sync)
    {
      if (!operation.IsCompleted) return false;
      activeOperation = "deletion";
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

  public void Cancel() { lock (sync) cancellation?.Cancel(); }
  public async Task StopAsync(CancellationToken cancellationToken)
  {
    Cancel();
    await operation.WaitAsync(cancellationToken);
  }
  public void Dispose() => cancellation?.Dispose();
}
