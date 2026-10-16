using System.Diagnostics;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Logs and times a stage of opening the engine, of background index maintenance or of
/// shutdown, without adding timers or logging to normal ingestion.
/// </summary>
sealed class StageLog : IDisposable
{
  readonly ILogger logger;
  readonly string stage;
  readonly string directory;
  readonly long started = Stopwatch.GetTimestamp();
  bool completed;

  public StageLog(ILogger logger, string stage, string directory, FormattableString metrics = null)
  {
    this.logger = logger;
    this.stage = stage;
    this.directory = directory;
    Write("started", metrics);
  }

  public void Progress(FormattableString metrics, long units, string unit) =>
      Write("progress", metrics, units, unit);

  public void Complete(FormattableString metrics, long? units = null, string unit = null)
  {
    completed = true;
    Write("completed", metrics, units, unit);
  }

  void Write(string state, FormattableString metrics = null, long? units = null, string unit = null)
  {
    var elapsed = Stopwatch.GetElapsedTime(started);
    var rate = units.HasValue
        ? FormattableString.Invariant($"; throughput={units.Value / Math.Max(elapsed.TotalSeconds, 0.000001):F1} {unit}/s")
        : "";
    var message = FormattableString.Invariant(
        $"{stage} {state}; directory={directory}; duration={elapsed.TotalMilliseconds:F1} ms")
        + (metrics == null ? "" : "; " + FormattableString.Invariant(metrics)) + rate;
    // A logging provider must not prevent recovery or mask the original storage exception.
    try { logger.LogInfo(message); } catch { }
  }

  public void Dispose()
  {
    if (!completed) Write("failed");
  }
}

/// <summary>Logs and times the disposal of a resource as a stage of shutdown.</summary>
sealed class StageDisposal(IDisposable resource, ILogger logger, string stage, string directory) : IDisposable
{
  public void Dispose()
  {
    using var log = new StageLog(logger, stage, directory);
    resource.Dispose();
    log.Complete(null);
  }
}
