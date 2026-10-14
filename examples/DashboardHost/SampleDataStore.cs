using ZoneTree.Analytics;

// This engine owns only generated data. Reporting leases protect it while a
// reset closes the engine and removes all raw events, rollups and metadata.
sealed class SampleDataStore(IWebHostEnvironment environment, IConfiguration configuration, TimeZoneInfo timeZone,
    ILogger<SampleDataStore> logger) : IAsyncDisposable
{
  public const string Site = DashboardAnalyticsOptions.PerformanceSite;
  public const string ApiPath = "/_analytics/sample-data/api";
  readonly object sync = new();
  readonly string directory = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "data/performance-samples"));
  AnalyticsEngine? engine;
  int workerCount = 1;
  int readers;
  bool resetting;
  TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
  const string Marker = ".zonetree-generated-samples";

  public bool HasRetainedData => File.Exists(Path.Combine(directory, Marker));

  public SampleEngineLease Acquire()
  {
    lock (sync)
    {
      if (resetting) throw new SampleDataBusyException();
      engine ??= Open();
      if (readers++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
      return new SampleEngineLease(engine, () =>
      {
        lock (sync) { if (--readers == 0) drained.TrySetResult(); }
      });
    }
  }

  AnalyticsEngine Open()
  {
    EnsureOwnedDirectory();
    return new AnalyticsEngine(DashboardAnalyticsOptions.CreatePerformanceSamples(directory, timeZone, workerCount, logger));
  }

  public async Task PrepareGenerationAsync(int workers, CancellationToken cancellationToken)
  {
    if (workers is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(workers));
    Task wait;
    AnalyticsEngine? current;
    lock (sync)
    {
      if (resetting) throw new SampleDataBusyException();
      if (engine == null) { workerCount = workers; return; }
      if (workerCount == workers) return;
      resetting = true;
      current = engine;
      wait = readers == 0 ? Task.CompletedTask : drained.Task;
    }
    try
    {
      await wait.WaitAsync(cancellationToken);
      await current.DisposeAsync();
      lock (sync) { engine = null; workerCount = workers; }
    }
    finally { lock (sync) resetting = false; }
  }

  void EnsureOwnedDirectory()
  {
    var primary = configuration["Analytics:DataDirectory"] ?? DashboardAnalyticsOptions.DefaultDataDirectory;
    if (!Path.IsPathRooted(primary)) primary = Path.Combine(environment.ContentRootPath, primary);
    primary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(primary));
    if (string.Equals(primary, directory, StringComparison.OrdinalIgnoreCase)
        || primary.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || directory.StartsWith(primary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("The sample store must be separate from the primary analytics directory.");
    for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
      if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
        throw new InvalidOperationException("The generated-sample directory must not use symbolic links or junctions.");
    if (Directory.Exists(directory) && !File.Exists(Path.Combine(directory, Marker)) && Directory.EnumerateFileSystemEntries(directory).Any())
      throw new InvalidOperationException("The sample directory contains files not owned by this sample generator.");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, Marker), "ZoneTree.Analytics DashboardHost generated samples");
  }

  public long ReadCount()
  {
    var path = Path.Combine(directory, "generated-count.txt");
    return File.Exists(path) && long.TryParse(File.ReadAllText(path), out var count) ? count : 0;
  }

  public async Task WriteCountAsync(long count)
  {
    var path = Path.Combine(directory, "generated-count.txt");
    await File.WriteAllTextAsync(path + ".tmp", count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.Move(path + ".tmp", path, overwrite: true);
  }

  public async Task ClearAsync(CancellationToken cancellationToken)
  {
    Task wait;
    AnalyticsEngine? current;
    lock (sync)
    {
      if (resetting) throw new SampleDataBusyException();
      // Acquire the directory lock before deletion, even after a host restart.
      engine ??= Open();
      resetting = true;
      current = engine;
      wait = readers == 0 ? Task.CompletedTask : drained.Task;
    }
    try
    {
      await wait.WaitAsync(cancellationToken);
      await current.DisposeAsync();
      lock (sync) engine = null;
      EnsureOwnedDirectory();
      Directory.Delete(directory, recursive: true);
    }
    finally { lock (sync) resetting = false; }
  }

  public async ValueTask DisposeAsync()
  {
    AnalyticsEngine? current;
    lock (sync) { resetting = true; current = engine; engine = null; }
    if (current != null) await current.DisposeAsync();
  }
}

