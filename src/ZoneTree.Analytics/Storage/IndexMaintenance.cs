using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Builds and repairs the indexes and rebuilds joint rollup history on one background thread,
/// while the engine ingests events and answers queries.
/// </summary>
/// <remarks>
/// <para>
/// The jobs run one after another, in the order given: a rollup index build folds in the history
/// a reconstruction rebuilt before it. Each job resumes from its own checkpoint, so a job that
/// fails is retried after a delay from where it stopped, and one interrupted by disposal or a
/// crash continues when the engine opens again. A job that throws
/// <see cref="AnalyticsStorageException"/> cannot succeed until then: it is not retried, the next
/// jobs still run, and <see cref="Completion"/> fails with it.
/// </para>
/// <para>
/// Until a job completes, queries use what does not depend on it: raw events instead of
/// dimension indexes or rebuilt history, primary rollups instead of rollup indexes.
/// </para>
/// </remarks>
sealed class IndexMaintenance : IDisposable
{
  readonly (string Name, Action<CancellationToken> Run)[] jobs;

  readonly ILogger logger;

  readonly string directory;

  readonly CancellationTokenSource stopping = new();

  readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

  Task task;

  /// <param name="jobs">The jobs, in the order they run.</param>
  /// <param name="logger">Receives, and counts, the failures of jobs; logs a stop that waits for one.</param>
  /// <param name="directory">The data directory, which its logs name.</param>
  public IndexMaintenance((string Name, Action<CancellationToken> Run)[] jobs, ILogger logger, string directory)
  {
    this.jobs = jobs;
    this.logger = logger;
    this.directory = directory;
  }

  /// <summary>The wait before a failed job runs again.</summary>
  internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Completes when every job has; fails when a job could not succeed in this run; canceled when
  /// the engine closes first.
  /// </summary>
  public Task Completion => completion.Task;

  /// <summary>The job that is running, or null.</summary>
  public string Current { get; private set; }

  public void Start()
  {
    if (jobs.Length == 0)
    {
      completion.TrySetResult();
      return;
    }
    task = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
  }

  void Run()
  {
    var token = stopping.Token;
    var failures = new List<AnalyticsStorageException>();
    foreach (var (name, run) in jobs)
    {
      Current = name;
      while (true)
      {
        try
        {
          run(token);
          break;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
          completion.TrySetCanceled(token);
          return;
        }
        catch (AnalyticsStorageException failure)
        {
          // The job cannot succeed until the engine opens again: the next jobs still run.
          try { logger.LogError(failure); } catch { }
          failures.Add(failure);
          break;
        }
        catch (Exception exception)
        {
          try { logger.LogError(exception); } catch { }
          if (token.WaitHandle.WaitOne(RetryDelay))
          {
            completion.TrySetCanceled(token);
            return;
          }
        }
      }
    }
    Current = null;
    if (failures.Count > 0)
      completion.TrySetException(failures);
    else
      completion.TrySetResult();
  }

  /// <summary>Stops the running job at its next checkpoint and waits for it.</summary>
  public void Dispose()
  {
    stopping.Cancel();
    if (task is { IsCompleted: false })
    {
      // The job resumes from its checkpoint when the engine opens again.
      using var log = new StageLog(logger, "Index maintenance shutdown", directory, $"running={Current}");
      task.GetAwaiter().GetResult();
      log.Complete(null);
    }
    completion.TrySetCanceled(stopping.Token);
    stopping.Dispose();
  }
}
