sealed record SignInRequest(string UserName, string Password);
sealed record ProfileRequest(string DisplayName);
sealed record PasswordRequest(string CurrentPassword, string NewPassword);

sealed record SiteView(string Id, string Name);

sealed record MeResponse(UserView User, IReadOnlyList<SiteView> Sites, string SessionId);

static class AuthEndpoints
{
  const string SignInScope = "signin";

  public static void MapDashboardAuth(this IEndpointRouteBuilder app)
  {
    var auth = app.MapGroup("/_dashboard/api/auth").WithDashboardErrors();
    auth.MapPost("/signin", SignInAsync).AllowAnonymous();
    auth.MapPost("/signout", SignOut).AllowAnonymous();
    var me = auth.MapGroup("/me").RequireAuthorization();
    me.MapGet("", (HttpContext context, SettingsService settings) => DashboardJson.Ok(Me(context, settings)));
    me.MapPut("/profile", UpdateProfileAsync);
    me.MapPost("/password", ChangePasswordAsync);
    me.MapGet("/sessions", (HttpContext context, SessionStore sessions) =>
        DashboardJson.Ok(sessions.List(DashboardAuthentication.User(context).Id, DashboardAuthentication.SessionId(context))));
    me.MapDelete("/sessions/{id}", (string id, HttpContext context, SessionStore sessions) =>
        sessions.Revoke(id, DashboardAuthentication.User(context).Id) ? Results.NoContent() : DashboardJson.Error("The session does not exist.", 404));
  }

  public static MeResponse Me(HttpContext context, SettingsService settings, UserRecord user = null, string sessionId = null)
  {
    user ??= DashboardAuthentication.User(context);
    var sites = settings.Current?.Sites.Items.Where(site => user.CanAccess(site.Id)).Select(site => new SiteView(site.Id, site.Name)).ToList() ?? [];
    return new(UserView.From(user), sites, sessionId ?? DashboardAuthentication.SessionId(context));
  }

  static async Task<IResult> SignInAsync(HttpContext context, UserStore users, SessionStore sessions, SignInThrottle throttle,
      SetupStatus setup, SettingsService settings, AuditLog audit)
  {
    if (!setup.Complete) return DashboardJson.Error("Finish the setup wizard first.", StatusCodes.Status409Conflict);
    var request = await DashboardJson.ReadAsync<SignInRequest>(context.Request);
    var userName = request.UserName?.Trim() ?? "";
    if (userName.Length > 64) userName = userName[..64];
    if (throttle.Begin(SignInScope, userName, context) is { } wait)
      return TooManyAttempts(context, wait);
    var user = users.FindByName(userName);
    var valid = await throttle.HashAsync(() => PasswordHasher.Verify(request.Password, user?.PasswordHash), context.RequestAborted)
        && user is { Disabled: false };
    if (!valid)
    {
      audit.Write(userName, "auth.signin-failed", user == null ? "Unknown user name." : user.Disabled ? "The account is disabled." : "Wrong password.", context);
      return DashboardJson.Error("The user name or password is incorrect.", StatusCodes.Status401Unauthorized);
    }
    throttle.Succeeded(SignInScope, userName, context);
    var (token, session) = sessions.Create(user, context);
    SessionStore.WriteCookie(context, token, session.Expires);
    users.RecordSignIn(user);
    audit.Write(user.UserName, "auth.signin", null, context);
    return DashboardJson.Ok(Me(context, settings, user, SessionStore.Id(token)));
  }

  public static IResult TooManyAttempts(HttpContext context, TimeSpan wait)
  {
    var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
    context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return DashboardJson.Error($"Too many failed attempts. Try again in {Math.Ceiling(seconds / 60.0)} minute(s).",
        StatusCodes.Status429TooManyRequests);
  }

  static IResult SignOut(HttpContext context, SessionStore sessions, AuditLog audit)
  {
    var token = context.Request.Cookies[SessionStore.CookieName];
    if (!string.IsNullOrEmpty(token) && sessions.TryAuthenticate(token, out var id, out var user))
    {
      sessions.Revoke(id, user.Id);
      audit.Write(user.UserName, "auth.signout", null, context);
    }
    SessionStore.DeleteCookie(context);
    return Results.NoContent();
  }

  static async Task<IResult> UpdateProfileAsync(HttpContext context, UserStore users, SettingsService settings)
  {
    var request = await DashboardJson.ReadAsync<ProfileRequest>(context.Request);
    var user = DashboardAuthentication.User(context);
    var updated = users.Update(user.Id, new UserChange(null, request.DisplayName ?? "", null, null, null, null, null), KnownSites(settings));
    return DashboardJson.Ok(Me(context, settings, updated));
  }

  // A new password ends every session, then signs this browser in again.
  static async Task<IResult> ChangePasswordAsync(HttpContext context, UserStore users, SessionStore sessions,
      SignInThrottle throttle, SettingsService settings, AuditLog audit)
  {
    var request = await DashboardJson.ReadAsync<PasswordRequest>(context.Request);
    var user = DashboardAuthentication.User(context);
    if (throttle.Begin("password", user.Id, context) is { } wait)
      return TooManyAttempts(context, wait);
    if (!await throttle.HashAsync(() => PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash), context.RequestAborted))
      return DashboardJson.Error("The current password is incorrect.");
    throttle.Succeeded("password", user.Id, context);
    var updated = users.Update(user.Id, new UserChange(null, null, null, null, null, null, request.NewPassword ?? ""), KnownSites(settings));
    sessions.RevokeAll(user.Id);
    var (token, session) = sessions.Create(updated, context);
    SessionStore.WriteCookie(context, token, session.Expires);
    audit.Write(user.UserName, "auth.password-changed", "Other sessions ended.", context);
    return DashboardJson.Ok(Me(context, settings, updated, SessionStore.Id(token)));
  }

  public static HashSet<string> KnownSites(SettingsService settings) =>
      settings.Current?.Sites.Items.Select(site => site.Id).ToHashSet(StringComparer.Ordinal) ?? [];
}
