#nullable enable
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZoneTree.Analytics;

// Only the latest successful run is retained. Publishing and deletion wait for
// report leases, so a query never loses its engine or directory mid-request.
sealed class BenchmarkReportStore : IAsyncDisposable
{
  public const string Site = "benchmark-results";
  public const string ApiPath = "/_analytics/sample-data/benchmark-api";
  readonly object sync = new();
  readonly string root;
  readonly ILogger? logger;
  Dataset? current;
  AnalyticsEngine? engine;
  int readers;
  bool switching;
  TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
  const string Marker = ".zonetree-benchmark";
  static readonly Action<ILogger, Exception?> CleanupWarning = LoggerMessage.Define(LogLevel.Warning,
      new EventId(1, "BenchmarkCleanup"), "Previous benchmark directory could not be removed.");
  sealed record Dataset(string Directory, string TimeZone, SampleBenchmarkResult Result);

  public BenchmarkReportStore(string contentRoot, ILogger? logger = null)
  {
    root = Path.GetFullPath(Path.Combine(contentRoot, "data/ingestion-benchmarks"));
    this.logger = logger;
    var manifest = Path.Combine(root, "latest.json");
    if (File.Exists(manifest))
    {
      current = JsonSerializer.Deserialize<Dataset>(File.ReadAllText(manifest));
      if (current != null) ValidateOwned(Path.Combine(root, current.Directory));
    }
  }

  public SampleBenchmarkResult? Result { get { lock (sync) return current?.Result; } }

  public string CreateRunDirectory()
  {
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    ValidatePath(directory);
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, Marker), "ZoneTree.Analytics benchmark");
    return directory;
  }

  public SampleEngineLease Acquire()
  {
    lock (sync)
    {
      if (switching) throw new SampleDataBusyException();
      if (current == null) throw new BenchmarkDataUnavailableException();
      var path = Path.Combine(root, current.Directory);
      ValidateOwned(path);
      engine ??= new AnalyticsEngine(CreateReportOptions(path, current));
      if (readers++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
      return new(engine, () => { lock (sync) { if (--readers == 0) drained.TrySetResult(); } });
    }
  }

  static AnalyticsOptions CreateReportOptions(string path, Dataset dataset)
  {
    var options = SampleBenchmark.CreateOptions(path, 1, dataset.Result.Percentiles);
    options.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(dataset.TimeZone);
    return options;
  }

  async Task BeginSwitchAsync(CancellationToken token)
  {
    Task wait;
    lock (sync)
    {
      if (switching) throw new SampleDataBusyException();
      switching = true;
      wait = readers == 0 ? Task.CompletedTask : drained.Task;
    }
    try
    {
      await wait.WaitAsync(token);
      if (engine != null) await engine.DisposeAsync();
      lock (sync) engine = null;
    }
    catch { lock (sync) switching = false; throw; }
  }

  public async Task PublishAsync(string directory, string timeZone, SampleBenchmarkResult result, CancellationToken token)
  {
    ValidateOwned(directory);
    await BeginSwitchAsync(token);
    try
    {
      var dataset = new Dataset(Path.GetFileName(directory), timeZone, result);
      var manifest = Path.Combine(root, "latest.json");
      await File.WriteAllTextAsync(manifest + ".tmp", JsonSerializer.Serialize(dataset), token);
      File.Move(manifest + ".tmp", manifest, overwrite: true);
      Dataset? previous;
      lock (sync) { previous = current; current = dataset; }
      if (previous != null)
      {
        try { DeleteRun(Path.Combine(root, previous.Directory)); }
        catch (IOException error) { if (logger != null) CleanupWarning(logger, error); }
        catch (UnauthorizedAccessException error) { if (logger != null) CleanupWarning(logger, error); }
      }
    }
    finally { lock (sync) switching = false; }
  }

  public async Task ClearAsync(CancellationToken token)
  {
    await BeginSwitchAsync(token);
    try
    {
      Dataset? previous;
      lock (sync) previous = current;
      if (previous == null) return;
      DeleteRun(Path.Combine(root, previous.Directory));
      File.Delete(Path.Combine(root, "latest.json"));
      lock (sync) current = null;
    }
    finally { lock (sync) switching = false; }
  }

  public void RemoveUnpublishedRun(string directory)
  {
    lock (sync) if (current?.Directory == Path.GetFileName(directory)) return;
    DeleteRun(directory);
  }

  void DeleteRun(string directory)
  {
    ValidateOwned(directory);
    Directory.Delete(directory, recursive: true);
  }

  void ValidateOwned(string directory)
  {
    ValidatePath(directory);
    if (!File.Exists(Path.Combine(directory, Marker)))
      throw new InvalidOperationException("The benchmark directory is not owned by this host.");
  }

  void ValidatePath(string directory)
  {
    if (Path.GetDirectoryName(Path.GetFullPath(directory)) != root
        || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
      throw new InvalidOperationException("The benchmark directory is outside its owned root.");
    for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
      if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
        throw new InvalidOperationException("The benchmark directory must not use symbolic links or junctions.");
  }

  public async ValueTask DisposeAsync()
  {
    lock (sync) switching = true;
    if (engine != null) await engine.DisposeAsync();
  }
}
