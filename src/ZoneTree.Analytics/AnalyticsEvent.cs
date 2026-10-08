namespace ZoneTree.Analytics;

/// <summary>
/// An event to record.
/// </summary>
/// <remarks>
/// Only <see cref="Name"/> is required. Everything else is optional context:
/// <list type="bullet">
/// <item><see cref="Url"/> and <see cref="Referrer"/> are turned into page, hostname,
/// UTM, source and channel dimensions.</item>
/// <item><see cref="IpAddress"/> and <see cref="UserAgent"/> are used to derive an anonymous
/// visitor hash, geography and browser, OS and device dimensions. They are never stored.</item>
/// <item><see cref="VisitorId"/> identifies a visitor explicitly, for example a logged-in user.
/// It is stored only as a keyed hash.</item>
/// </list>
/// An event instance must not be modified after it is passed to the engine.
/// </remarks>
public sealed class AnalyticsEvent
{
  /// <summary>
  /// Creates an empty event. Set <see cref="Name"/> before tracking it.
  /// </summary>
  public AnalyticsEvent()
  {
  }

  /// <summary>
  /// Creates an event with the given name.
  /// </summary>
  public AnalyticsEvent(string name)
  {
    Name = name;
  }

  /// <summary>
  /// The event name, for example "pageview", "signup" or "order_completed".
  /// Names starting with '$' are reserved for engine-generated events.
  /// </summary>
  public string Name { get; set; }

  /// <summary>
  /// The time the event happened. Defaults to the current UTC time.
  /// Local and unspecified times are converted to UTC.
  /// </summary>
  public DateTime? Timestamp { get; set; }

  /// <summary>
  /// The site, application or tenant the event belongs to.
  /// Defaults to <see cref="AnalyticsOptions.DefaultSite"/>.
  /// </summary>
  public string Site { get; set; }

  /// <summary>
  /// An explicit, stable visitor identifier such as a user id or a first-party cookie value.
  /// When set, it takes precedence over IP and user-agent based anonymous hashing.
  /// The identifier itself is never stored; only a keyed hash is.
  /// </summary>
  public string VisitorId { get; set; }

  /// <summary>
  /// The client IP address. Used only in memory for anonymous visitor hashing
  /// and geolocation. It is never stored.
  /// </summary>
  public string IpAddress { get; set; }

  /// <summary>
  /// The client user agent. Used only in memory for anonymous visitor hashing,
  /// bot filtering and browser, OS and device detection. It is never stored.
  /// </summary>
  public string UserAgent { get; set; }

  /// <summary>
  /// The page URL the event happened on. Absolute URLs and paths are accepted.
  /// </summary>
  public string Url { get; set; }

  /// <summary>
  /// The referrer URL of the page.
  /// </summary>
  public string Referrer { get; set; }

  /// <summary>
  /// Custom dimensions (properties) of the event. Values set here for standard
  /// dimensions (for example "country") override automatic enrichment.
  /// </summary>
  public IDictionary<string, string> Dimensions { get; set; }

  /// <summary>
  /// Custom numeric measurements of the event, for example "revenue" or "duration_ms".
  /// </summary>
  public IDictionary<string, double> Metrics { get; set; }

  /// <summary>
  /// Marks a custom event as non-interactive. Non-interactive events extend
  /// sessions but do not prevent a bounce. Page views are always interactive.
  /// </summary>
  public bool NonInteractive { get; set; }

  /// <summary>
  /// Creates a page view event.
  /// </summary>
  public static AnalyticsEvent PageView(
      string url,
      string referrer = null,
      string ipAddress = null,
      string userAgent = null,
      string site = null)
  {
    return new AnalyticsEvent(StandardEvents.PageView)
    {
      Url = url,
      Referrer = referrer,
      IpAddress = ipAddress,
      UserAgent = userAgent,
      Site = site
    };
  }

  /// <summary>
  /// Adds or replaces a dimension and returns this event.
  /// </summary>
  public AnalyticsEvent WithDimension(string name, string value)
  {
    Dimensions ??= new Dictionary<string, string>(StringComparer.Ordinal);
    Dimensions[name] = value;
    return this;
  }

  /// <summary>
  /// Adds or replaces a metric and returns this event.
  /// </summary>
  public AnalyticsEvent WithMetric(string name, double value)
  {
    Metrics ??= new Dictionary<string, double>(StringComparer.Ordinal);
    Metrics[name] = value;
    return this;
  }
}
