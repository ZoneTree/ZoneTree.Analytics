using Microsoft.Extensions.Logging;
using ZoneTree.Logging;

namespace ZoneTree.Analytics.AspNetCore;

/// <summary>Logging configuration for engines created outside dependency injection.</summary>
public static class AnalyticsLoggingExtensions
{
  /// <summary>
  /// Routes engine and storage messages to the supplied host logger, respecting its filters.
  /// </summary>
  public static AnalyticsOptions UseLogger(this AnalyticsOptions options, ILogger logger)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(logger);
    options.Storage.Logger = new LoggerAdapter(logger);
    return options;
  }
}

/// <summary>
/// Routes engine and storage logs to Microsoft.Extensions.Logging.
/// </summary>
sealed class LoggerAdapter : IZoneTreeLogger
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
  public ZoneTreeLogLevel Level { get; set; } = ZoneTreeLogLevel.Trace;

  public bool IsEnabled(ZoneTreeLogLevel level) => IsEnabled(level, HostLevel(level));

  bool IsEnabled(ZoneTreeLogLevel level, LogLevel target) =>
      target != LogLevel.None && level >= Level && logger.IsEnabled(target);

  public void Log(ZoneTreeLogLevel level, string message, Exception exception = null)
  {
    var target = HostLevel(level);
    if (IsEnabled(level, target))
      logger.Log(target, default, message, exception, static (state, _) => state ?? "");
  }

  static LogLevel HostLevel(ZoneTreeLogLevel level) => level switch
  {
    ZoneTreeLogLevel.Trace => LogLevel.Trace,
    ZoneTreeLogLevel.Info => LogLevel.Information,
    ZoneTreeLogLevel.Warning => LogLevel.Warning,
    ZoneTreeLogLevel.Error => LogLevel.Error,
    _ => LogLevel.None
  };
}
