using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The logger handed to ZoneTree. It counts the errors ZoneTree reports on its own threads, for
/// example failed merges or segment cleanups, in <see cref="AnalyticsStatistics.Errors"/>.
/// </summary>
sealed class CountingLogger : ILogger
{
  readonly ILogger inner;

  readonly Counters counters;

  public CountingLogger(ILogger inner, Counters counters)
  {
    this.inner = inner;
    this.counters = counters;
  }

  public LogLevel LogLevel
  {
    get => inner.LogLevel;
    set => inner.LogLevel = value;
  }

  public void LogError(Exception log)
  {
    Interlocked.Increment(ref counters.Errors);
    inner.LogError(log);
  }

  public void LogWarning(object log) => inner.LogWarning(log);

  public void LogInfo(object log) => inner.LogInfo(log);

  public void LogTrace(object log) => inner.LogTrace(log);
}
