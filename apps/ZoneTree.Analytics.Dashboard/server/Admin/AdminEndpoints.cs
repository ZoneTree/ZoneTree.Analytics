sealed record SettingsSaveRequest(DashboardSettings Settings, bool ConfirmDataLoss);
sealed record SettingsReviewRequest(DashboardSettings Settings);
sealed record ChangeView(string Key, string Label, SettingTier Tier);

// The administrator's API: settings, users and their sessions, engine status and the audit log.
static class AdminEndpoints
{
  public static void MapDashboardAdmin(this IEndpointRouteBuilder app)
  {
    var admin = app.MapGroup("/_dashboard/api/admin").WithDashboardErrors()
        .RequireAuthorization(DashboardAuthentication.AdministratorPolicy);

    admin.MapGet("/schema", () => DashboardJson.Ok(SettingsSchema.Document));
    admin.MapGet("/settings", (SettingsService settings) =>
        DashboardJson.Ok(new { settings = settings.Current, restartRequired = settings.RestartRequired }));
    admin.MapPost("/settings/review", async (HttpContext context, SettingsService settings) =>
    {
      var request = await DashboardJson.ReadAsync<SettingsReviewRequest>(context.Request);
      var review = settings.Review(request.Settings);
      return DashboardJson.Ok(new { changes = Changes(review.Changes), warnings = review.Warnings });
    });
    admin.MapPut("/settings", SaveSettingsAsync);
    admin.MapGet("/status", (SettingsService settings, EngineHost engines, UserStore users, SystemStore store) =>
        DashboardJson.Ok(new
        {
          engine = engines.GetStatus(includePartitions: true),
          restartRequired = settings.RestartRequired,
          dataDirectory = settings.Current == null ? null : SettingsMapper.ResolveDataDirectory(settings.Current, settings.ContentRoot),
          systemDirectory = store.Directory,
          users = users.List().Count,
          sites = settings.Current?.Sites.Items.Count ?? 0,
        }));

    admin.MapGet("/users", (UserStore users) => DashboardJson.Ok(users.List().Select(UserView.From)));
    admin.MapPost("/users", async (HttpContext context, UserStore users, SettingsService settings, AuditLog audit) =>
    {
      var change = await DashboardJson.ReadAsync<UserChange>(context.Request);
      var user = users.Create(change, AuthEndpoints.KnownSites(settings));
      audit.Write(Actor(context), "user.created", $"{user.UserName} ({user.Role})", context);
      return DashboardJson.Ok(UserView.From(user));
    });
    admin.MapPut("/users/{id}", async (string id, HttpContext context, UserStore users, SettingsService settings, AuditLog audit) =>
    {
      var change = await DashboardJson.ReadAsync<UserChange>(context.Request);
      var user = users.Update(id, change, AuthEndpoints.KnownSites(settings));
      audit.Write(Actor(context), "user.updated", Describe(user.UserName, change), context);
      return DashboardJson.Ok(UserView.From(user));
    });
    admin.MapDelete("/users/{id}", (string id, HttpContext context, UserStore users, SessionStore sessions, AuditLog audit) =>
    {
      var user = users.Get(id) ?? throw new UserStoreException("The user does not exist.");
      users.Delete(id);
      sessions.RevokeAll(id);
      audit.Write(Actor(context), "user.deleted", user.UserName, context);
      return Results.NoContent();
    });
    admin.MapGet("/users/{id}/sessions", (string id, HttpContext context, SessionStore sessions) =>
        DashboardJson.Ok(sessions.List(id, DashboardAuthentication.SessionId(context))));
    admin.MapDelete("/users/{id}/sessions", (string id, HttpContext context, UserStore users, SessionStore sessions, AuditLog audit) =>
    {
      var user = users.Get(id) ?? throw new UserStoreException("The user does not exist.");
      sessions.RevokeAll(id, id == DashboardAuthentication.User(context).Id ? DashboardAuthentication.SessionId(context) : null);
      audit.Write(Actor(context), "user.sessions-ended", user.UserName, context);
      return Results.NoContent();
    });
    admin.MapDelete("/sessions/{id}", (string id, HttpContext context, SessionStore sessions, AuditLog audit) =>
    {
      if (!sessions.Revoke(id, null)) return DashboardJson.Error("The session does not exist.", StatusCodes.Status404NotFound);
      audit.Write(Actor(context), "session.ended", null, context);
      return Results.NoContent();
    });

    admin.MapGet("/audit", (string before, int? take, AuditLog audit) => DashboardJson.Ok(audit.List(before, take ?? 100)));
  }

  static async Task<IResult> SaveSettingsAsync(HttpContext context, SettingsService settings)
  {
    var request = await DashboardJson.ReadAsync<SettingsSaveRequest>(context.Request);
    try
    {
      var result = await settings.SaveAsync(request.Settings, Actor(context), request.ConfirmDataLoss, context);
      return DashboardJson.Ok(new
      {
        settings = result.Settings,
        changes = Changes(result.Changes),
        reopened = result.Reopened,
        restartRequired = result.RestartRequired,
      });
    }
    catch (Exception error) when (error is not (SettingsValidationException or SettingsConfirmationRequiredException
        or DashboardRequestException))
    {
      return DashboardJson.Error($"The analytics engine could not open with these settings, so the previous settings stay in use: {error.Message}",
          StatusCodes.Status422UnprocessableEntity);
    }
  }

  static IEnumerable<ChangeView> Changes(IEnumerable<SettingChange> changes) =>
      changes.Select(change => new ChangeView(change.Key, change.Label, change.Tier));

  static string Actor(HttpContext context) => DashboardAuthentication.User(context)?.UserName;

  static string Describe(string userName, UserChange change)
  {
    var parts = new List<string>();
    if (change.UserName != null) parts.Add("user name");
    if (change.DisplayName != null) parts.Add("display name");
    if (change.Role != null) parts.Add($"role {change.Role}");
    if (change.AllSites != null || change.Sites != null) parts.Add("sites");
    if (change.Disabled != null) parts.Add(change.Disabled.Value ? "disabled" : "enabled");
    if (change.Password != null) parts.Add("password reset");
    return $"{userName}: {string.Join(", ", parts)}";
  }
}
