using ZoneTree.Analytics;

sealed record SetupKeyRequest(string Key);
sealed record SetupValidateRequest(string Key, DashboardSettings Settings);
sealed record SetupAdministrator(string UserName, string DisplayName, string Password);
sealed record SetupCompleteRequest(string Key, SetupAdministrator Administrator, DashboardSettings Settings);

// The first-run wizard's API. Every call that reveals or changes anything requires the setup key
// from the server log; all of it stops working once the administrator exists.
static class SetupEndpoints
{
  const string Scope = "setup";
  static readonly SemaphoreSlim completing = new(1, 1);

  public static void MapDashboardSetup(this IEndpointRouteBuilder app)
  {
    var setup = app.MapGroup("/_dashboard/api/setup").WithDashboardErrors().AllowAnonymous();
    setup.MapGet("/status", (SetupStatus status, EngineHost engines) =>
        DashboardJson.Ok(new { complete = status.Complete, engine = engines.State }));
    setup.MapPost("/verify", async (HttpContext context, SetupStatus status, SetupKey key, SignInThrottle throttle) =>
    {
      var request = await DashboardJson.ReadAsync<SetupKeyRequest>(context.Request);
      return Verify(context, status, key, throttle, request.Key) ?? DashboardJson.Ok(new
      {
        settings = SettingsService.CreateDefaults(),
        schema = SettingsSchema.Document,
      });
    });
    setup.MapPost("/validate", async (HttpContext context, SetupStatus status, SetupKey key, SignInThrottle throttle,
        SettingsService settings) =>
    {
      var request = await DashboardJson.ReadAsync<SetupValidateRequest>(context.Request);
      if (Verify(context, status, key, throttle, request.Key) is { } failure) return failure;
      settings.Validate(request.Settings);
      return DashboardJson.Ok(new { valid = true });
    });
    setup.MapPost("/complete", CompleteAsync);
  }

  static IResult Verify(HttpContext context, SetupStatus status, SetupKey key, SignInThrottle throttle, string candidate)
  {
    if (status.Complete) return DashboardJson.Error("Setup is already complete. Sign in instead.", StatusCodes.Status409Conflict);
    if (throttle.Paused(Scope, null, context) is { } wait) return AuthEndpoints.TooManyAttempts(context, wait);
    if (key.Matches(candidate))
    {
      throttle.Succeeded(Scope, null, context);
      return null;
    }
    throttle.Failed(Scope, null, context);
    return DashboardJson.Error("The setup key does not match. Copy it from the server log.", StatusCodes.Status403Forbidden);
  }

  static async Task<IResult> CompleteAsync(HttpContext context, SetupStatus status, SetupKey key, SignInThrottle throttle,
      SettingsService settings, UserStore users, SessionStore sessions, AuditLog audit, EngineHost engines,
      IConfiguration configuration, ILogger<SetupKey> logger)
  {
    var request = await DashboardJson.ReadAsync<SetupCompleteRequest>(context.Request);
    await completing.WaitAsync(context.RequestAborted);
    try
    {
      if (Verify(context, status, key, throttle, request.Key) is { } failure) return failure;
      var administrator = request.Administrator ?? throw new DashboardRequestException("Create the administrator account.");
      var change = new UserChange(administrator.UserName, administrator.DisplayName, UserRole.Administrator, true, null, null,
          administrator.Password ?? "");
      UserStore.ValidateNew(change);
      settings.Validate(request.Settings);
      try
      {
        await settings.CompleteSetupAsync(request.Settings);
      }
      catch (Exception error) when (error is not SettingsValidationException)
      {
        return DashboardJson.Error($"The analytics engine could not open with these settings: {error.Message}",
            StatusCodes.Status422UnprocessableEntity);
      }
      var user = users.Create(change, AuthEndpoints.KnownSites(settings));
      key.Retire();
      status.MarkComplete();
      var (token, session) = sessions.Create(user, context);
      SessionStore.WriteCookie(context, token, session.Expires);
      users.RecordSignIn(user);
      audit.Write(user.UserName, "setup.completed", $"{request.Settings.Sites.Items.Count} site(s), data in {request.Settings.General.DataDirectory}.", context);
      logger.LogInformation("Setup complete. The administrator is {UserName}.", user.UserName);
      if (configuration.GetValue<bool>("Analytics:SeedDemo"))
      {
        using var lease = await engines.AcquireAsync(context.RequestAborted);
        if (lease.Engine.GetEventNames(DateRange.LastDays(60)).Count == 0)
          await SampleTraffic.SeedAsync(lease.Engine);
      }
      return DashboardJson.Ok(AuthEndpoints.Me(context, settings, user, SessionStore.Id(token)));
    }
    finally { completing.Release(); }
  }
}
