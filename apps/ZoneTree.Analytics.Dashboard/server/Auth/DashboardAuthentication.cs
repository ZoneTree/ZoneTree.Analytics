using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

static class DashboardAuthentication
{
  public const string Scheme = "ZoneTree";
  public const string AdministratorPolicy = "Administrator";
  public const string QueryPolicy = "Query";
  const string UserItem = "zt-dashboard-user";
  const string SessionItem = "zt-dashboard-session";

  public static UserRecord User(HttpContext context) => context.Items[UserItem] as UserRecord;

  public static string SessionId(HttpContext context) => context.Items[SessionItem] as string;

  public static void SetUser(HttpContext context, UserRecord user, string sessionId)
  {
    context.Items[UserItem] = user;
    context.Items[SessionItem] = sessionId;
  }

  public static ClaimsPrincipal Principal(UserRecord user) => new(new ClaimsIdentity(
  [
    new Claim(ClaimTypes.NameIdentifier, user.Id),
    new Claim(ClaimTypes.Name, user.UserName),
    new Claim(ClaimTypes.Role, user.Role.ToString()),
  ], Scheme));

  public static IServiceCollection AddDashboardAuthentication(this IServiceCollection services)
  {
    services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, DashboardAuthenticationHandler>(Scheme, null);
    services.AddAuthorizationBuilder()
        .AddPolicy(AdministratorPolicy, policy => policy.RequireRole(nameof(UserRole.Administrator)))
        .AddPolicy(QueryPolicy, policy => policy.RequireRole(nameof(UserRole.Administrator), nameof(UserRole.Analyst)));
    return services;
  }
}

// Authenticates the session cookie against the system store. API challenges answer JSON.
sealed class DashboardAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, SessionStore sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
  protected override Task<AuthenticateResult> HandleAuthenticateAsync()
  {
    if (DashboardPaths.IsStaticAsset(Request.Path)) return Task.FromResult(AuthenticateResult.NoResult());
    var token = Request.Cookies[SessionStore.CookieName];
    if (string.IsNullOrEmpty(token)) return Task.FromResult(AuthenticateResult.NoResult());
    if (!sessions.TryAuthenticate(token, out var id, out var user))
    {
      SessionStore.DeleteCookie(Context);
      return Task.FromResult(AuthenticateResult.NoResult());
    }
    DashboardAuthentication.SetUser(Context, user, id);
    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(DashboardAuthentication.Principal(user), Scheme.Name)));
  }

  protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
  {
    Response.StatusCode = StatusCodes.Status401Unauthorized;
    Response.Headers.CacheControl = "no-store";
    await Response.WriteAsJsonAsync(new { error = "Sign in to continue." });
  }

  protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
  {
    Response.StatusCode = StatusCodes.Status403Forbidden;
    Response.Headers.CacheControl = "no-store";
    await Response.WriteAsJsonAsync(new { error = "Your role does not allow this. Ask the administrator for access." });
  }
}
