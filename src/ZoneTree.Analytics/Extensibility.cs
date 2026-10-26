using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics;

// Extension points follow one model:
// - each is an interface with a single method,
// - implementations must be thread-safe: they are called concurrently,
// - each has a static helper of the same name without the "I" that offers the built-in
//   implementation (if any) and a Create(...) factory for lambdas,
// - an exception thrown by an extension is logged and counted in
//   AnalyticsStatistics.Errors, and the event is processed as if the extension had not
//   been there: analytics never breaks the host application.

/// <summary>
/// Decides whether an event is tracked at all.
/// </summary>
/// <remarks>
/// Filters run inside <see cref="AnalyticsEngine.Track(AnalyticsEvent)"/> on the calling thread,
/// before the event is queued; keep them fast. Typical uses: excluding internal IP ranges,
/// honoring opt-out cookies or Global Privacy Control.
/// </remarks>
public interface IEventFilter
{
  /// <summary>
  /// Returns false to discard the event.
  /// </summary>
  bool ShouldTrack(AnalyticsEvent analyticsEvent);
}

/// <summary>
/// Factories for <see cref="IEventFilter"/>.
/// </summary>
public static class EventFilter
{
  /// <summary>
  /// Creates a filter from a predicate.
  /// </summary>
  public static IEventFilter Create(Func<AnalyticsEvent, bool> shouldTrack)
  {
    ArgumentNullException.ThrowIfNull(shouldTrack);
    return new DelegateEventFilter(shouldTrack);
  }

  sealed class DelegateEventFilter(Func<AnalyticsEvent, bool> shouldTrack) : IEventFilter
  {
    public bool ShouldTrack(AnalyticsEvent analyticsEvent) => shouldTrack(analyticsEvent);
  }
}

/// <summary>
/// What an extension can read about an event being processed.
/// </summary>
public class EventContext
{
  internal EventContext()
  {
  }

  /// <summary>The event name as tracked.</summary>
  public string EventName { get; internal set; }

  /// <summary>When the event happened (UTC).</summary>
  public DateTimeOffset Timestamp { get; internal set; }

  /// <summary>The site.</summary>
  public string Site { get; internal set; }

  /// <summary>The page URL, or null.</summary>
  public string Url { get; internal set; }

  /// <summary>The referrer URL, or null.</summary>
  public string Referrer { get; internal set; }

  /// <summary>The client IP address, or null. Available in memory only; never stored.</summary>
  public string IpAddress { get; internal set; }

  /// <summary>The user agent, or null. Available in memory only; never stored.</summary>
  public string UserAgent { get; internal set; }

  /// <summary>The parsed user agent, or null without a user agent.</summary>
  public UserAgentInfo UserAgentInfo { get; internal set; }

  /// <summary>The custom dimensions of the event as tracked.</summary>
  public IReadOnlyDictionary<string, string> TrackedDimensions =>
      trackedDimensions ??= TrackedDimensionSource == null
          ? EmptyDimensions
          : TrackedDimensionSource
              .Where(x => x.Value != null)
              .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

  // Built on first use: most extensions never read the tracked dimensions.
  internal KeyValuePair<string, string>[] TrackedDimensionSource;

  IReadOnlyDictionary<string, string> trackedDimensions;

  static readonly IReadOnlyDictionary<string, string> EmptyDimensions = new Dictionary<string, string>();
}

/// <summary>
/// A recognized bot.
/// </summary>
/// <param name="Name">The bot name, for example "Googlebot", "GPTBot" or "curl".</param>
/// <param name="Category">The bot category, see <see cref="BotCategories"/>.</param>
public sealed record BotInfo(string Name, string Category);

/// <summary>
/// Recognizes bot traffic.
/// </summary>
/// <remarks>
/// Detectors run on ingestion threads in the order of <see cref="WebOptions.BotDetectors"/>;
/// the first one that returns a <see cref="BotInfo"/> wins.
/// </remarks>
public interface IBotDetector
{
  /// <summary>
  /// Returns the bot that produced the event, or null for human traffic.
  /// </summary>
  BotInfo Detect(EventContext context);
}

/// <summary>
/// Built-in bot detectors and factories for <see cref="IBotDetector"/>.
/// </summary>
public static class BotDetector
{
  /// <summary>
  /// The built-in detector: recognizes search engines, AI crawlers, SEO tools, link previews,
  /// uptime monitors, browser automation and HTTP libraries by their user agent.
  /// </summary>
  public static IBotDetector UserAgent => UserAgentBotDetector.Instance;

  /// <summary>
  /// Creates a detector from a function that returns the bot, or null for human traffic.
  /// </summary>
  public static IBotDetector Create(Func<EventContext, BotInfo> detect)
  {
    ArgumentNullException.ThrowIfNull(detect);
    return new DelegateBotDetector(detect);
  }

  sealed class DelegateBotDetector(Func<EventContext, BotInfo> detect) : IBotDetector
  {
    public BotInfo Detect(EventContext context) => detect(context);
  }
}

/// <summary>
/// Browser, operating system and device information parsed from a user agent.
/// </summary>
/// <param name="Browser">The browser name, or empty when unknown.</param>
/// <param name="BrowserVersion">The browser major version, or empty.</param>
/// <param name="OperatingSystem">The operating system name, or empty when unknown.</param>
/// <param name="OperatingSystemVersion">The operating system version, or empty.</param>
/// <param name="Device">"Desktop", "Mobile" or "Tablet".</param>
public sealed record UserAgentInfo(
    string Browser,
    string BrowserVersion,
    string OperatingSystem,
    string OperatingSystemVersion,
    string Device);

/// <summary>
/// Parses user agents into browser, operating system and device.
/// </summary>
public interface IUserAgentParser
{
  /// <summary>
  /// Parses a user agent. Returns null when nothing can be determined.
  /// </summary>
  UserAgentInfo Parse(string userAgent);
}

/// <summary>
/// The built-in user agent parser and factories for <see cref="IUserAgentParser"/>.
/// </summary>
public static class UserAgentParser
{
  /// <summary>
  /// The built-in, dependency-free parser.
  /// </summary>
  public static IUserAgentParser Default => BuiltInUserAgentParser.Instance;

  /// <summary>
  /// Creates a parser from a function, for example an adapter for a third-party library.
  /// </summary>
  public static IUserAgentParser Create(Func<string, UserAgentInfo> parse)
  {
    ArgumentNullException.ThrowIfNull(parse);
    return new DelegateUserAgentParser(parse);
  }

  sealed class DelegateUserAgentParser(Func<string, UserAgentInfo> parse) : IUserAgentParser
  {
    public UserAgentInfo Parse(string userAgent) => parse(userAgent);
  }
}

/// <summary>
/// A resolved location. Any part can be null.
/// </summary>
/// <param name="Country">The country. ISO 3166-1 alpha-2 codes are recommended.</param>
/// <param name="Region">The region or subdivision.</param>
/// <param name="City">The city.</param>
public sealed record GeoLocation(string Country, string Region = null, string City = null);

/// <summary>
/// Resolves IP addresses to locations.
/// </summary>
/// <remarks>
/// <see cref="IpCountryDatabase"/> resolves countries from a downloadable MMDB database. For region
/// or city, adapt another provider, or pass geography through event dimensions, for example from
/// a CDN header.
/// </remarks>
public interface IGeoLocator
{
  /// <summary>
  /// Resolves the location of an IP address. Returns null when unknown.
  /// </summary>
  GeoLocation Locate(string ipAddress);
}

/// <summary>
/// Factories for <see cref="IGeoLocator"/>.
/// </summary>
public static class GeoLocator
{
  /// <summary>
  /// Creates a locator from a function.
  /// </summary>
  public static IGeoLocator Create(Func<string, GeoLocation> locate)
  {
    ArgumentNullException.ThrowIfNull(locate);
    return new DelegateGeoLocator(locate);
  }

  sealed class DelegateGeoLocator(Func<string, GeoLocation> locate) : IGeoLocator
  {
    public GeoLocation Locate(string ipAddress) => locate(ipAddress);
  }
}

/// <summary>
/// What an enricher can read and change about an event being processed.
/// </summary>
/// <remarks>
/// Dimensions read and written here are the final ones: custom dimensions, page and hostname,
/// and session-level dimensions such as source, country or device. A session-level value set
/// on the first event of a session applies to the whole session; on later events of the session
/// it has no effect, because session-level dimensions are fixed when the session starts.
/// Values are sanitized and truncated when set. After all enrichers run, custom event
/// dimensions are filtered by the privacy allow/deny lists and the per-event count limit.
/// Each enricher reads its own pending changes. They are committed only when it returns
/// successfully; a throwing enricher's changes are discarded before the next enricher runs.
/// </remarks>
public sealed class EnrichmentContext : EventContext
{
  internal Func<string, string> Getter;

  internal Action<string, string> Setter;

  internal EnrichmentContext()
  {
  }

  /// <summary>The bot that produced the event, or null for human traffic.</summary>
  public BotInfo Bot { get; internal set; }

  /// <summary>
  /// Returns a dimension value, or null when the event does not carry it.
  /// </summary>
  public string GetDimension(string name) => Getter(name);

  /// <summary>
  /// Sets a dimension. Null removes it. <see cref="StandardDimensions.EntryPage"/> and the bot
  /// dimensions are maintained by the engine and cannot be set.
  /// </summary>
  /// <remarks>
  /// Custom event dimensions remain subject to <see cref="PrivacyOptions.AllowedDimensions"/>,
  /// <see cref="PrivacyOptions.DeniedDimensions"/> and <see cref="PrivacyOptions.MaxCustomDimensions"/>
  /// after all enrichers run. Page, hostname and standard session dimensions are exempt from
  /// these custom-dimension rules; engine-maintained bot dimensions are preserved on bot events.
  /// Custom session names must be allowed at configuration time and do not consume the event limit.
  /// </remarks>
  public void SetDimension(string name, string value) => Setter(name, value);
}

/// <summary>
/// Adds, changes or removes dimensions of events after the built-in enrichment.
/// </summary>
/// <remarks>
/// Enrichers run on ingestion threads in the order of <see cref="AnalyticsOptions.Enrichers"/>,
/// for human and bot events. Typical uses: normalizing page paths, deriving dimensions such as
/// a content section from the path, or setting geography from a header value.
/// </remarks>
public interface IEventEnricher
{
  /// <summary>
  /// Enriches one event.
  /// </summary>
  void Enrich(EnrichmentContext context);
}

/// <summary>
/// Factories for <see cref="IEventEnricher"/>.
/// </summary>
public static class EventEnricher
{
  /// <summary>
  /// Creates an enricher from an action.
  /// </summary>
  public static IEventEnricher Create(Action<EnrichmentContext> enrich)
  {
    ArgumentNullException.ThrowIfNull(enrich);
    return new DelegateEventEnricher(enrich);
  }

  /// <summary>
  /// Creates an enricher that rewrites the page path, for example to collapse ids:
  /// <c>path =&gt; Regex.Replace(path, @"/\d+", "/:id")</c>. The entry page follows.
  /// </summary>
  public static IEventEnricher NormalizePage(Func<string, string> normalize)
  {
    ArgumentNullException.ThrowIfNull(normalize);
    return new DelegateEventEnricher(context =>
    {
      var page = context.GetDimension(StandardDimensions.Page);
      if (page != null)
        context.SetDimension(StandardDimensions.Page, normalize(page) ?? page);
    });
  }

  sealed class DelegateEventEnricher(Action<EnrichmentContext> enrich) : IEventEnricher
  {
    public void Enrich(EnrichmentContext context) => enrich(context);
  }
}
