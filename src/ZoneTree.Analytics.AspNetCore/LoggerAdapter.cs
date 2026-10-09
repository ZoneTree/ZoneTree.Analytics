using Microsoft.Extensions.Logging;
using HostLogLevel = Microsoft.Extensions.Logging.LogLevel;
using ZoneTreeLogLevel = ZoneTree.Logger.LogLevel;

namespace ZoneTree.Analytics.AspNetCore;

/// <summary>
/// Routes engine and storage logs to Microsoft.Extensions.Logging.
/// </summary>
sealed class LoggerAdapter : ZoneTree.Logger.ILogger
{
  readonly ILogger logger;

  public LoggerAdapter(ILogger logger)
  {
    this.logger = logger;
  }

  /// <summary>
  /// The minimum level ZoneTree formats messages for. Filtering is left to the
  /// Microsoft.Extensions.Logging configuration.
  /// </summary>
  public ZoneTreeLogLevel LogLevel { get; set; } = ZoneTreeLogLevel.Trace;

  public void LogError(Exception log) =>
      Log(ZoneTreeLogLevel.Error, HostLogLevel.Error, log?.Message, log);

  public void LogWarning(object log) =>
      Log(ZoneTreeLogLevel.Warning, HostLogLevel.Warning, log, null);

  public void LogInfo(object log) =>
      Log(ZoneTreeLogLevel.Info, HostLogLevel.Information, log, null);

  public void LogTrace(object log) =>
      Log(ZoneTreeLogLevel.Trace, HostLogLevel.Trace, log, null);

  void Log(ZoneTreeLogLevel level, HostLogLevel target, object message, Exception exception)
  {
    if (LogLevel > level || !logger.IsEnabled(target))
      return;
    logger.Log(target, default, message, exception, static (state, _) => state?.ToString() ?? "");
  }
}
