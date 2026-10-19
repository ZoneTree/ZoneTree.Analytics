using System.Collections.Concurrent;

// Pauses sign-in after repeated failures, for a user name from one address and, with a larger
// limit, for the address whatever names it tries. Kept in memory: a restart clears it.
sealed class SignInThrottle(SettingsService settings)
{
  const int AddressLimitFactor = 4;

  sealed class Attempts
  {
    public int Failures;
    public DateTime WindowStart;
    public DateTime PausedUntil;
  }

  readonly ConcurrentDictionary<string, Attempts> attempts = new(StringComparer.Ordinal);

  // Password hashing is deliberately slow: at most this many run at once, so a flood of
  // attempts waits in line instead of taking every processor.
  readonly SemaphoreSlim hashing = new(Math.Max(1, Environment.ProcessorCount / 2));

  static string NameKey(string scope, string name, HttpContext context) =>
      scope + "\nname\n" + (name ?? "").Trim().ToLowerInvariant() + "\n" + context.Connection.RemoteIpAddress;

  static string AddressKey(string scope, HttpContext context) => scope + "\naddress\n" + context.Connection.RemoteIpAddress;

  // Counts the attempt before its credentials are checked, so attempts sent at the same time
  // cannot all get through. Returns the time left in a pause, or null when the attempt may go on.
  public TimeSpan? Begin(string scope, string name, HttpContext context)
  {
    var security = settings.Security;
    var now = DateTime.UtcNow;
    var window = TimeSpan.FromMinutes(security.SignInLockoutMinutes);
    var byName = attempts.GetOrAdd(NameKey(scope, name, context), _ => new Attempts { WindowStart = now });
    var byAddress = attempts.GetOrAdd(AddressKey(scope, context), _ => new Attempts { WindowStart = now });
    // Checking and counting happen under both locks, always taken name first, then address.
    lock (byName)
      lock (byAddress)
      {
        var wait = Max(byName.PausedUntil - now, byAddress.PausedUntil - now);
        if (wait > TimeSpan.Zero) return wait;
        Count(byName, security.MaxFailedSignIns, now, window);
        Count(byAddress, security.MaxFailedSignIns * AddressLimitFactor, now, window);
      }
    if (attempts.Count > 100_000) Prune(now, window);
    return null;
  }

  // A successful attempt clears the name's count and is taken back from the address's count.
  public void Succeeded(string scope, string name, HttpContext context)
  {
    attempts.TryRemove(NameKey(scope, name, context), out _);
    if (attempts.TryGetValue(AddressKey(scope, context), out var byAddress))
      lock (byAddress)
        if (byAddress.Failures > 0) byAddress.Failures--;
  }

  public async Task<T> HashAsync<T>(Func<T> work, CancellationToken cancellationToken)
  {
    await hashing.WaitAsync(cancellationToken);
    try { return work(); }
    finally { hashing.Release(); }
  }

  static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

  // The caller holds the entry's lock.
  static void Count(Attempts entry, int limit, DateTime now, TimeSpan window)
  {
    if (now - entry.WindowStart > window) { entry.Failures = 0; entry.WindowStart = now; }
    if (++entry.Failures < limit) return;
    entry.PausedUntil = now + window;
    entry.Failures = 0;
    entry.WindowStart = now;
  }

  void Prune(DateTime now, TimeSpan window)
  {
    foreach (var (key, entry) in attempts)
      lock (entry)
        if (entry.PausedUntil < now && now - entry.WindowStart > window)
          attempts.TryRemove(key, out _);
  }
}
