using Microsoft.AspNetCore.Http;

namespace ZoneTree.Analytics.AspNetCore;

/// <summary>
/// ASP.NET Core integration options.
/// </summary>
public sealed class AnalyticsWebOptions
{
  /// <summary>
  /// Path prefix of the tracker script and beacon endpoint. Default is "/_analytics".
  /// The script is served at {prefix}/script.js and events are posted to {prefix}/event.
  /// </summary>
  public string PathPrefix { get; set; } = "/_analytics";

  /// <summary>
  /// Resolves the site of a request. Default is null, which uses
  /// <see cref="AnalyticsOptions.DefaultSite"/>. Use it to separate hosts or tenants,
  /// for example <c>context => context.Request.Host.Host</c>.
  /// </summary>
  public Func<HttpContext, string> SiteResolver { get; set; }

  /// <summary>
  /// Resolves an explicit visitor id, for example the authenticated user id.
  /// Default is null: visitors are identified anonymously.
  /// </summary>
  public Func<HttpContext, string> VisitorIdResolver { get; set; }

  /// <summary>
  /// Decides whether a request is tracked by the server-side middleware.
  /// Runs after the response is produced. Default is null: every successful HTML page view
  /// outside <see cref="ExcludedPaths"/> is tracked.
  /// </summary>
  public Func<HttpContext, bool> ShouldTrack { get; set; }

  /// <summary>
  /// Path prefixes the middleware never tracks. The <see cref="PathPrefix"/> is always excluded.
  /// </summary>
  public List<string> ExcludedPaths { get; } = ["/api", "/health", "/favicon.ico"];

  /// <summary>
  /// Event names accepted from the browser beacon. Null accepts any non-reserved name.
  /// </summary>
  public ISet<string> AllowedClientEvents { get; set; }

  /// <summary>
  /// Maximum beacon payload size in bytes. Default is 8 KiB.
  /// </summary>
  public int MaxPayloadBytes { get; set; } = 8 * 1024;

  /// <summary>
  /// Authorizes access to a site through the reporting API (the site argument is null for the
  /// default site). Default is null: every site is allowed, so protect the API itself, for example
  /// with <c>RequireAuthorization()</c>.
  /// </summary>
  public Func<HttpContext, string, bool> AuthorizeSite { get; set; }
}
