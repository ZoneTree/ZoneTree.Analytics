using System.Security.Cryptography;
using System.Text;

sealed class SessionRecord
{
  public string UserId { get; set; }
  public string SecurityStamp { get; set; }
  public DateTime Created { get; set; }
  public DateTime LastSeen { get; set; }
  public DateTime Expires { get; set; }
  public string IpAddress { get; set; }
  public string UserAgent { get; set; }
}

// The id is the SHA-256 of the token: it names a session without granting access.
sealed record SessionView(string Id, DateTime Created, DateTime LastSeen, DateTime Expires, string IpAddress,
    string UserAgent, bool Current);

// Sessions are random 256-bit tokens kept in a cookie; the store keeps only their SHA-256.
sealed class SessionStore(SystemStore store, UserStore users, SettingsService settings)
{
  const string Prefix = "session/";
  public const string CookieName = "zt_session";
  static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

  public static string Id(string token) =>
      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

  public (string Token, SessionRecord Session) Create(UserRecord user, HttpContext context)
  {
    var token = Base64Url(RandomNumberGenerator.GetBytes(32));
    var now = DateTime.UtcNow;
    var security = settings.Security;
    var session = new SessionRecord
    {
      UserId = user.Id,
      SecurityStamp = user.SecurityStamp,
      Created = now,
      LastSeen = now,
      Expires = now.AddDays(security.SessionLifetimeDays),
      IpAddress = context.Connection.RemoteIpAddress?.ToString(),
      UserAgent = Truncate(context.Request.Headers.UserAgent.ToString(), 300),
    };
    store.Put(Prefix + Id(token), session);
    return (token, session);
  }

  public bool TryAuthenticate(string token, out string id, out UserRecord user)
  {
    user = null;
    id = null;
    if (string.IsNullOrEmpty(token) || token.Length > 100) return false;
    id = Id(token);
    if (!store.TryGet<SessionRecord>(Prefix + id, out var session)) return false;
    var now = DateTime.UtcNow;
    if (IsExpired(session, now))
    {
      store.Delete(Prefix + id);
      return false;
    }
    user = users.Get(session.UserId);
    if (user == null || user.Disabled || user.SecurityStamp != session.SecurityStamp)
    {
      store.Delete(Prefix + id);
      user = null;
      return false;
    }
    if (now - session.LastSeen >= TouchInterval)
    {
      session.LastSeen = now;
      store.Put(Prefix + id, session);
    }
    return true;
  }

  bool IsExpired(SessionRecord session, DateTime now) =>
      now >= session.Expires || now - session.LastSeen >= TimeSpan.FromHours(settings.Security.SessionIdleHours);

  public List<SessionView> List(string userId, string currentId) =>
      [.. store.List<SessionRecord>(Prefix)
          .Where(entry => entry.Value.UserId == userId && !IsExpired(entry.Value, DateTime.UtcNow))
          .Select(entry => View(entry.Key[Prefix.Length..], entry.Value, currentId))
          .OrderByDescending(session => session.LastSeen)];

  static SessionView View(string id, SessionRecord session, string currentId) =>
      new(id, session.Created, session.LastSeen, session.Expires, session.IpAddress, session.UserAgent, id == currentId);

  public bool Revoke(string id, string userId)
  {
    if (string.IsNullOrEmpty(id) || !store.TryGet<SessionRecord>(Prefix + id, out var session)) return false;
    if (userId != null && session.UserId != userId) return false;
    store.Delete(Prefix + id);
    return true;
  }

  public void RevokeAll(string userId, string exceptId = null)
  {
    foreach (var entry in store.List<SessionRecord>(Prefix))
      if (entry.Value.UserId == userId && entry.Key[Prefix.Length..] != exceptId)
        store.Delete(entry.Key);
  }

  // Removes expired sessions and the sessions of users that no longer exist or whose sessions ended.
  public int Cleanup()
  {
    var now = DateTime.UtcNow;
    var removed = 0;
    foreach (var entry in store.List<SessionRecord>(Prefix))
    {
      var user = users.Get(entry.Value.UserId);
      if (!IsExpired(entry.Value, now) && user != null && !user.Disabled && user.SecurityStamp == entry.Value.SecurityStamp)
        continue;
      store.Delete(entry.Key);
      removed++;
    }
    return removed;
  }

  public static void WriteCookie(HttpContext context, string token, DateTime expires) =>
      context.Response.Cookies.Append(CookieName, token, new CookieOptions
      {
        HttpOnly = true,
        // Lax keeps links into the dashboard signed in; unsafe requests also need a matching Origin.
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        Path = "/",
        Expires = expires,
        IsEssential = true,
      });

  public static void DeleteCookie(HttpContext context) =>
      context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax });

  static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  static string Truncate(string value, int length) => string.IsNullOrEmpty(value) || value.Length <= length ? value : value[..length];
}

sealed class SessionCleanup(SessionStore sessions, ILogger<SessionCleanup> logger) : BackgroundService
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
    }
    while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
  }
}
