// Hourly housekeeping of the system store: ended sessions, and audit entries past their retention.
sealed class SystemCleanup(SessionStore sessions, AuditLog audit, SettingsService settings, ILogger<SystemCleanup> logger)
    : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
    do
    {
      try
      {
        var removed = sessions.Cleanup();
        if (removed > 0) logger.LogInformation("Removed {Count} ended dashboard sessions.", removed);
      }
      catch (Exception error) { logger.LogError(error, "Dashboard session cleanup failed."); }
      try
      {
        var retention = TimeSpan.FromDays(settings.Security.AuditRetentionDays);
        var removed = audit.Prune(DateTime.UtcNow - retention);
        if (removed > 0) logger.LogInformation("Removed {Count} audit log entries past their retention.", removed);
      }
      catch (Exception error) { logger.LogError(error, "Audit log cleanup failed."); }
    }
    while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
  }
}
