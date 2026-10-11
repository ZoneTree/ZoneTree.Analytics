using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZoneTree.Analytics.AspNetCore;

/// <summary>
/// Dependency injection registration.
/// </summary>
public static class AnalyticsServiceCollectionExtensions
{
  /// <summary>
  /// Registers a singleton <see cref="AnalyticsEngine"/>, also as <see cref="IAnalyticsTracker"/>,
  /// and the web integration options.
  /// The engine is disposed (and flushed) when the host shuts down. A relative
  /// <see cref="AnalyticsOptions.DataDirectory"/> is resolved against the content root, and engine
  /// logs go to Microsoft.Extensions.Logging under the "ZoneTree.Analytics" category unless
  /// <see cref="StorageOptions.Logger"/> is set.
  /// </summary>
  public static IServiceCollection AddZoneTreeAnalytics(
      this IServiceCollection services,
      Action<AnalyticsOptions> configure = null,
      Action<AnalyticsWebOptions> configureWeb = null)
  {
    ArgumentNullException.ThrowIfNull(services);
    var options = new AnalyticsOptions();
    configure?.Invoke(options);
    var webOptions = new AnalyticsWebOptions();
    configureWeb?.Invoke(webOptions);
    services.AddSingleton(webOptions);
    services.AddSingleton(provider =>
    {
      var environment = provider.GetService<IHostEnvironment>();
      if (environment != null && !Path.IsPathRooted(options.DataDirectory))
        options.DataDirectory = Path.Combine(environment.ContentRootPath, options.DataDirectory);
      if (options.Storage.Logger == null && provider.GetService<ILoggerFactory>() is { } loggerFactory)
        options.Storage.Logger = new LoggerAdapter(loggerFactory.CreateLogger("ZoneTree.Analytics"));
      return new AnalyticsEngine(options);
    });
    services.AddSingleton<IAnalyticsTracker>(provider => provider.GetRequiredService<AnalyticsEngine>());
    return services;
  }
}

/// <summary>
/// Middleware and endpoint registration.
/// </summary>
public static class AnalyticsApplicationBuilderExtensions
{
  /// <summary>
  /// Tracks successful HTML page views on the server. Place it early in the pipeline,
  /// after forwarded-headers middleware so the client IP is correct.
  /// Use either this middleware or the JavaScript tracker for page views, not both.
  /// </summary>
  public static IApplicationBuilder UseZoneTreeAnalytics(this IApplicationBuilder app)
  {
    ArgumentNullException.ThrowIfNull(app);
    return app.UseMiddleware<PageViewTrackingMiddleware>();
  }

  /// <summary>
  /// Maps the JavaScript tracker at {prefix}/script.js and the beacon endpoint at
  /// {prefix}/event. Returns the route group, for example to add rate limiting.
  /// </summary>
  /// <example>
  /// <code>
  /// &lt;script defer src="/_analytics/script.js"&gt;&lt;/script&gt;
  /// &lt;script&gt;zta('signup', { plan: 'pro' })&lt;/script&gt;
  /// </code>
  /// </example>
  public static RouteGroupBuilder MapZoneTreeAnalytics(this IEndpointRouteBuilder endpoints)
  {
    ArgumentNullException.ThrowIfNull(endpoints);
    var options = endpoints.ServiceProvider.GetRequiredService<AnalyticsWebOptions>();
    var group = endpoints.MapGroup(options.PathPrefix);
    group.MapGet("/script.js", (RequestDelegate)TrackerEndpoints.ServeScript);
    group.MapPost("/event", (RequestDelegate)TrackerEndpoints.ReceiveEvent);
    return group;
  }
}

sealed class PageViewTrackingMiddleware
{
  readonly RequestDelegate next;

  readonly AnalyticsEngine engine;

  readonly AnalyticsWebOptions options;

  public PageViewTrackingMiddleware(RequestDelegate next, AnalyticsEngine engine, AnalyticsWebOptions options)
  {
    this.next = next;
    this.engine = engine;
    this.options = options;
  }

  public async Task InvokeAsync(HttpContext context)
  {
    await next(context);
    try
    {
      if (!ShouldTrack(context))
        return;
      var request = context.Request;
      var analyticsEvent = RequestEvents.Create(context, options, StandardEvents.PageView);
      analyticsEvent.Url = request.GetDisplayUrl();
      analyticsEvent.Referrer = request.Headers.Referer;
      engine.Track(analyticsEvent);
    }
    catch (Exception exception)
    {
      engine.ReportIntegrationError(exception);
    }
  }

  bool ShouldTrack(HttpContext context)
  {
    var request = context.Request;
    if (!HttpMethods.IsGet(request.Method))
      return false;
    if (context.Response.StatusCode != StatusCodes.Status200OK)
      return false;
    var contentType = context.Response.ContentType;
    if (contentType == null || !contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
      return false;
    var path = request.Path;
    if (path.StartsWithSegments(options.PathPrefix, StringComparison.OrdinalIgnoreCase))
      return false;
    foreach (var excluded in options.ExcludedPaths)
    {
      if (path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
        return false;
    }
    // Speculative loads are not page views.
    var purpose = request.Headers["Sec-Purpose"].ToString() + request.Headers["Purpose"].ToString();
    if (purpose.Contains("prefetch", StringComparison.OrdinalIgnoreCase))
      return false;
    return options.ShouldTrack?.Invoke(context) ?? true;
  }
}

static class RequestEvents
{
  public static AnalyticsEvent Create(HttpContext context, AnalyticsWebOptions options, string name)
  {
    return new AnalyticsEvent(name)
    {
      Site = options.SiteResolver?.Invoke(context),
      VisitorId = options.VisitorIdResolver?.Invoke(context),
      IpAddress = context.Connection.RemoteIpAddress?.ToString(),
      UserAgent = context.Request.Headers.UserAgent
    };
  }
}

static class TrackerEndpoints
{
  static readonly Lazy<byte[]> Script = new(() =>
  {
    using var stream = typeof(TrackerEndpoints).Assembly
        .GetManifestResourceStream("ZoneTree.Analytics.AspNetCore.tracker.js");
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
  });

  public static Task ServeScript(HttpContext context)
  {
    var response = context.Response;
    response.ContentType = "application/javascript; charset=utf-8";
    response.Headers.CacheControl = "public, max-age=86400";
    var bytes = Script.Value;
    response.ContentLength = bytes.Length;
    return response.Body.WriteAsync(bytes).AsTask();
  }

  /// <summary>
  /// Receives a beacon: {"n": name, "u": url, "r": referrer, "p": {props}, "m": {values}}.
  /// </summary>
  public static async Task ReceiveEvent(HttpContext context)
  {
    var options = context.RequestServices.GetRequiredService<AnalyticsWebOptions>();
    var engine = context.RequestServices.GetRequiredService<AnalyticsEngine>();
    var request = context.Request;
    if (request.ContentLength > options.MaxPayloadBytes)
    {
      context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
      return;
    }
    var body = await ReadBodyAsync(request, options.MaxPayloadBytes, context.RequestAborted);
    if (body == null)
    {
      context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
      return;
    }
    AnalyticsEvent analyticsEvent;
    try
    {
      analyticsEvent = Parse(body, context, options);
      analyticsEvent?.Validate();
    }
    catch (Exception exception) when (exception is JsonException or ArgumentException)
    {
      analyticsEvent = null;
    }
    if (analyticsEvent == null)
    {
      context.Response.StatusCode = StatusCodes.Status400BadRequest;
      return;
    }
    engine.Track(analyticsEvent);
    context.Response.StatusCode = StatusCodes.Status202Accepted;
  }

  static async Task<byte[]> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
  {
    using var memory = new MemoryStream();
    var buffer = new byte[4096];
    while (true)
    {
      var read = await request.Body.ReadAsync(buffer, cancellationToken);
      if (read == 0)
        return memory.ToArray();
      if (memory.Length + read > limit)
        return null;
      memory.Write(buffer, 0, read);
    }
  }

  static AnalyticsEvent Parse(byte[] body, HttpContext context, AnalyticsWebOptions options)
  {
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
      return null;
    if (!root.TryGetProperty("n", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
      return null;
    var name = nameElement.GetString();
    if (string.IsNullOrWhiteSpace(name) || name.StartsWith('$'))
      return null;
    if (options.AllowedClientEvents != null && !options.AllowedClientEvents.Contains(name))
      return null;

    var analyticsEvent = RequestEvents.Create(context, options, name);
    analyticsEvent.Url = GetString(root, "u");
    analyticsEvent.HashRouting = root.TryGetProperty("h", out var hashRouting) && hashRouting.ValueKind == JsonValueKind.True;
    analyticsEvent.Referrer = GetString(root, "r");
    if (root.TryGetProperty("p", out var props) && props.ValueKind == JsonValueKind.Object)
    {
      foreach (var property in props.EnumerateObject())
      {
        var value = property.Value.ValueKind switch
        {
          JsonValueKind.String => property.Value.GetString(),
          JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
          _ => null
        };
        if (value != null)
          analyticsEvent.WithDimension(property.Name, value);
      }
    }
    if (root.TryGetProperty("m", out var values) && values.ValueKind == JsonValueKind.Object)
    {
      foreach (var property in values.EnumerateObject())
      {
        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
          analyticsEvent.WithValue(property.Name, number);
      }
    }
    return analyticsEvent;
  }

  static string GetString(JsonElement root, string name) =>
      root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
          ? element.GetString()
          : null;
}
