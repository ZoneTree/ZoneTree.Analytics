using ZoneTree.Analytics;

enum EngineState { NotConfigured, Opening, Running, Reopening, Failed }

sealed class EngineUnavailableException(string message) : Exception(message);

sealed record PartitionView(string Directory, long? Records);

// The IP location database of the engine: null fields until one is loaded.
sealed record IpLocationView(string DatabaseType, DateTimeOffset? BuildTime, int Ranges, string Precision,
    DateTimeOffset? LastChecked, string LastError);

sealed record EngineStatus(EngineState State, string Error, DateTime? OpenedAt, string IndexMaintenance,
    long EventsAccepted, long EventsProcessed, long EventsDropped, long QueueLength, long Errors,
    int OpenPartitions, IReadOnlyList<PartitionView> Partitions, IpLocationView IpLocation = null);

// Owns the primary analytics engine. Requests hold a lease while they use it; reopening waits
// for the leases to end, closes the engine (which flushes it) and opens it with new options.
sealed class EngineHost(IWebHostEnvironment environment, ILoggerFactory loggers) : IAsyncDisposable
{
  static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(30);
  readonly Lock sync = new();
  readonly SemaphoreSlim transitions = new(1, 1);
  readonly ILogger logger = loggers.CreateLogger<EngineHost>();
  readonly ILogger engineLogger = loggers.CreateLogger("ZoneTree.Analytics");
  AnalyticsEngine engine;
  EngineState state = EngineState.NotConfigured;
  string error;
  DateTime? openedAt;
  int readers;
  TaskCompletionSource drained = Completed();
  TaskCompletionSource settled = Completed();
  bool disposed;

  static TaskCompletionSource Completed()
  {
    var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    source.SetResult();
    return source;
  }

  public EngineState State { get { lock (sync) return state; } }

  // The IP location provider of the primary engine now, or null.
  public IpLocationProvider IpLocation { get { lock (sync) return engine?.IpLocation; } }

  public async Task<EngineLease> AcquireAsync(CancellationToken cancellationToken)
  {
    while (true)
    {
      Task wait;
      lock (sync)
      {
        switch (state)
        {
          case EngineState.Running:
            if (readers++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new EngineLease(engine, Release);
          case EngineState.NotConfigured:
            throw new EngineUnavailableException("Analytics is not set up yet. Finish the setup wizard.");
          case EngineState.Failed:
            throw new EngineUnavailableException("The analytics engine could not open. The administrator can see the error in the admin panel.");
          default:
            wait = settled.Task;
            break;
        }
      }
      try
      {
        await wait.WaitAsync(AcquireTimeout, cancellationToken).ConfigureAwait(false);
      }
      catch (TimeoutException)
      {
        throw new EngineUnavailableException("The analytics engine is applying new settings. Try again shortly.");
      }
    }
  }

  void Release()
  {
    lock (sync) if (--readers == 0) drained.TrySetResult();
  }

  public async Task OpenAsync(DashboardSettings settings)
  {
    await transitions.WaitAsync().ConfigureAwait(false);
    try
    {
      Begin(EngineState.Opening);
      await TryOpenAsync(settings).ConfigureAwait(false);
    }
    finally { transitions.Release(); }
  }

  // Opens the engine with the next settings. When they fail to open, the previous settings are
  // opened again and the error is rethrown.
  public async Task ReopenAsync(DashboardSettings next, DashboardSettings previous)
  {
    await transitions.WaitAsync().ConfigureAwait(false);
    try
    {
      Task wait;
      AnalyticsEngine current;
      lock (sync)
      {
        current = engine;
        state = engine == null ? EngineState.Opening : EngineState.Reopening;
        settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        wait = readers == 0 ? Task.CompletedTask : drained.Task;
      }
      await wait.ConfigureAwait(false);
      if (current != null)
      {
        await current.DisposeAsync().ConfigureAwait(false);
        lock (sync) engine = null;
      }
      var failure = await TryOpenAsync(next).ConfigureAwait(false);
      if (failure == null) return;
      if (previous != null)
      {
        Begin(EngineState.Opening);
        await TryOpenAsync(previous).ConfigureAwait(false);
      }
      throw failure;
    }
    finally { transitions.Release(); }
  }

  void Begin(EngineState opening)
  {
    lock (sync)
    {
      state = opening;
      settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
  }

  async Task<Exception> TryOpenAsync(DashboardSettings settings)
  {
    try
    {
      var options = SettingsMapper.ToOptions(settings, environment.ContentRootPath, engineLogger);
      // Opening reads metadata and decides index work; keep it off the request threads.
      var opened = await Task.Run(() => new AnalyticsEngine(options)).ConfigureAwait(false);
      lock (sync)
      {
        if (disposed)
        {
          _ = opened.DisposeAsync();
          return new ObjectDisposedException(nameof(EngineHost));
        }
        engine = opened;
        state = EngineState.Running;
        error = null;
        openedAt = DateTime.UtcNow;
        settled.TrySetResult();
      }
      logger.LogInformation("Analytics engine opened at {Directory}.", options.DataDirectory);
      return null;
    }
    catch (Exception exception)
    {
      logger.LogError(exception, "The analytics engine could not open.");
      lock (sync)
      {
        state = EngineState.Failed;
        error = exception.Message;
        settled.TrySetResult();
      }
      return exception;
    }
  }

  public EngineStatus GetStatus(bool includePartitions)
  {
    AnalyticsEngine current;
    EngineState currentState;
    string currentError;
    DateTime? opened;
    lock (sync) { current = engine; currentState = state; currentError = error; opened = openedAt; }
    AnalyticsStatistics statistics = null;
    try { statistics = current?.GetStatistics(includePartitions); }
    catch (ObjectDisposedException) { }
    return new(currentState, currentError, opened, statistics?.IndexMaintenance,
        statistics?.EventsAccepted ?? 0, statistics?.EventsProcessed ?? 0, statistics?.EventsDropped ?? 0,
        statistics?.QueueLength ?? 0, statistics?.Errors ?? 0, statistics?.OpenPartitions ?? 0,
        statistics?.Partitions.Select(partition => new PartitionView(partition.Directory, partition.ApproximateRecordCount)).ToList() ?? [],
        current?.IpLocation is { } provider ? IpLocationStatus(provider) : null);
  }

  static IpLocationView IpLocationStatus(IpLocationProvider provider)
  {
    var database = provider.Database;
    return new(database?.DatabaseType, database?.BuildTime, database?.RangeCount ?? 0, database?.Precision.ToString(),
        provider.LastChecked, provider.LastError);
  }

  public async ValueTask DisposeAsync()
  {
    AnalyticsEngine current;
    lock (sync)
    {
      disposed = true;
      current = engine;
      engine = null;
      state = EngineState.NotConfigured;
      settled.TrySetResult();
    }
    if (current != null) await current.DisposeAsync().ConfigureAwait(false);
  }
}

sealed class EngineLease(AnalyticsEngine engine, Action release) : IDisposable
{
  Action onRelease = release;
  public AnalyticsEngine Engine { get; } = engine;
  public void Dispose() => Interlocked.Exchange(ref onRelease, null)?.Invoke();
}

