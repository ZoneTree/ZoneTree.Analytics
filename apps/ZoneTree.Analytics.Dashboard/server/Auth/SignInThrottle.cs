using System.Collections.Concurrent;

// Pauses sign-in for a user name and client address after repeated failures. Kept in memory:
// a restart clears it.
sealed class SignInThrottle(SettingsService settings)
{
  sealed class Attempts
  {
    public int Failures;
    public DateTime WindowStart;
    public DateTime PausedUntil;
  }

  readonly ConcurrentDictionary<string, Attempts> attempts = new(StringComparer.Ordinal);

  static string Key(string scope, string name, HttpContext context) =>
      scope + "\n" + (name ?? "").Trim().ToLowerInvariant() + "\n" + context.Connection.RemoteIpAddress;

  // The time left in a pause, or null when an attempt may proceed.
  public TimeSpan? Paused(string scope, string name, HttpContext context)
  {
    if (!attempts.TryGetValue(Key(scope, name, context), out var entry)) return null;
    lock (entry)
    {
      var left = entry.PausedUntil - DateTime.UtcNow;
      return left > TimeSpan.Zero ? left : null;
    }
  }

  public void Failed(string scope, string name, HttpContext context)
  {
    var security = settings.Security;
    var now = DateTime.UtcNow;
    var window = TimeSpan.FromMinutes(security.SignInLockoutMinutes);
    var entry = attempts.GetOrAdd(Key(scope, name, context), _ => new Attempts { WindowStart = now });
    lock (entry)
    {
      if (now - entry.WindowStart > window) { entry.Failures = 0; entry.WindowStart = now; }
      if (++entry.Failures >= security.MaxFailedSignIns)
      {
        entry.PausedUntil = now + window;
        entry.Failures = 0;
        entry.WindowStart = now;
      }
    }
    if (attempts.Count > 100_000) Prune(now, window);
  }

  public void Succeeded(string scope, string name, HttpContext context) => attempts.TryRemove(Key(scope, name, context), out _);

  void Prune(DateTime now, TimeSpan window)
  {
    foreach (var (key, entry) in attempts)
      lock (entry)
        if (entry.PausedUntil < now && now - entry.WindowStart > window)
          attempts.TryRemove(key, out _);
  }
}
