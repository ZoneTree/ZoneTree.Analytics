static class DashboardPaths
{
  // Scripts, styles, fonts and images: files other than pages, outside the dashboard's APIs.
  // They are public and need no session.
  public static bool IsStaticAsset(PathString path)
  {
    var value = path.Value ?? "";
    if (value.StartsWith("/_dashboard/", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith(EngineMiddleware.ApiPath + "/", StringComparison.OrdinalIgnoreCase))
      return false;
    var extension = Path.GetExtension(value);
    return extension.Length > 0 && !extension.Equals(".html", StringComparison.OrdinalIgnoreCase);
  }
}

// Whether the setup wizard has created the administrator. Cached: checked on every page request.
sealed class SetupStatus(UserStore users, SettingsService settings)
{
  volatile bool complete = settings.IsConfigured && users.HasAdministrator;

  public bool Complete => complete;

  public void MarkComplete() => complete = true;
}

// Rejects state-changing dashboard and reporting requests sent from other sites. The session
// cookie is SameSite=Lax; this covers the rest.
sealed class SameOriginMiddleware(RequestDelegate next)
{
  public Task InvokeAsync(HttpContext context)
  {
    var request = context.Request;
    if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)
        || !(request.Path.StartsWithSegments("/_dashboard") || request.Path.StartsWithSegments(EngineMiddleware.ApiPath)
            || request.Path.StartsWithSegments("/_analytics/sample-data")))
      return next(context);
    var origin = request.Headers.Origin.ToString();
    var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
    if ((origin.Length > 0 && origin != $"{request.Scheme}://{request.Host}") || fetchSite is "cross-site" or "same-site")
    {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      return context.Response.WriteAsJsonAsync(new { error = "Use the dashboard from this host." });
    }
    return next(context);
  }
}

// Sends page requests where they belong: to the setup wizard until it is finished, to sign-in
// without a session, and away from pages the user's role cannot use. APIs authorize themselves.
sealed class PageGateMiddleware(RequestDelegate next, SetupStatus setup)
{
  static readonly HashSet<string> PublicPages = new(StringComparer.OrdinalIgnoreCase) { "/signin/", "/signout/" };

  public Task InvokeAsync(HttpContext context)
  {
    var request = context.Request;
    var path = request.Path.Value ?? "/";
    if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) || path.StartsWith("/_", StringComparison.Ordinal))
      return next(context);
    if (DashboardPaths.IsStaticAsset(request.Path))
      return next(context);
    var page = PageOf(path);
    if (!setup.Complete)
      return page == "/setup/" ? next(context) : Redirect(context, "/setup/");
    if (page == "/setup/") return Redirect(context, "/");
    if (PublicPages.Contains(page)) return next(context);
    var user = DashboardAuthentication.User(context);
    if (user == null)
      return Redirect(context, "/signin/?returnUrl=" + Uri.EscapeDataString(path + request.QueryString));
    if (!user.IsAdministrator && (page.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase) || page == "/sample-data/"))
      return Redirect(context, "/");
    if (!user.CanQuery && page == "/explorer/")
      return Redirect(context, "/");
    return next(context);
  }

  // "/admin/users/index.html" and "/admin/users" → "/admin/users/".
  static string PageOf(string path)
  {
    if (path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)) path = path[..^"index.html".Length];
    else if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) path = path[..^".html".Length] + "/";
    return path.EndsWith('/') ? path : path + "/";
  }

  static Task Redirect(HttpContext context, string location)
  {
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Redirect(location);
    return Task.CompletedTask;
  }
}
