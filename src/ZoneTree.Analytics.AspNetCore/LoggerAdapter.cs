using Microsoft.Extensions.Logging;
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

  public void LogError(Exception log)
  {
    if (LogLevel <= ZoneTreeLogLevel.Error)
      logger.LogError(log, "{Message}", log?.Message);
  }

  public void LogWarning(object log)
  {
    if (LogLevel <= ZoneTreeLogLevel.Warning)
      logger.LogWarning("{Message}", log);
  }

  public void LogInfo(object log)
  {
    if (LogLevel <= ZoneTreeLogLevel.Info)
      logger.LogInformation("{Message}", log);
  }

  public void LogTrace(object log)
  {
    if (LogLevel <= ZoneTreeLogLevel.Trace)
      logger.LogTrace("{Message}", log);
  }
}
