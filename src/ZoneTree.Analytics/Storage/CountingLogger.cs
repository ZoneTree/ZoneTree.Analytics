using ZoneTree.Logging;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The logger handed to ZoneTree. It counts the errors ZoneTree reports on its own threads, for
/// example failed merges or segment cleanups, in <see cref="AnalyticsStatistics.Errors"/>.
/// </summary>
sealed class CountingLogger : IZoneTreeLogger
{
  readonly IZoneTreeLogger inner;

  readonly Counters counters;

  public CountingLogger(IZoneTreeLogger inner, Counters counters)
  {
    this.inner = inner;
    this.counters = counters;
  }

  public ZoneTreeLogLevel Level
  {
    get => inner.Level;
    set => inner.Level = value;
  }

  // Keep error counts independent of the log sink's filters.
  public bool IsEnabled(ZoneTreeLogLevel level) => level == ZoneTreeLogLevel.Error || inner.IsEnabled(level);

  public void Log(ZoneTreeLogLevel level, string message, Exception exception = null)
  {
    if (level == ZoneTreeLogLevel.Error)
      Interlocked.Increment(ref counters.Errors);
    if (inner.IsEnabled(level))
      inner.Log(level, message, exception);
  }
}
