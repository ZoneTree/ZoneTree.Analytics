namespace ZoneTree.Analytics;

/// <summary>
/// Standard event names.
/// </summary>
public static class StandardEvents
{
  /// <summary>
  /// A page view.
  /// </summary>
  public const string PageView = "pageview";

  /// <summary>
  /// The engine-generated session event. Session metrics such as <see cref="Metric.Sessions"/>
  /// and <see cref="Metric.BounceRate"/> read it; as a base event, <see cref="Metric.Events"/>
  /// counts sessions.
  /// </summary>
  public const string Session = "$session";

  /// <summary>
  /// The engine-generated bot event, recorded for bot traffic when
  /// <see cref="WebOptions.Bots"/> is <see cref="BotTraffic.TrackSeparately"/>. It carries
  /// <see cref="StandardDimensions.Bot"/>, <see cref="StandardDimensions.BotCategory"/>,
  /// <see cref="StandardDimensions.Event"/>, page and hostname. <see cref="Metric.BotHits"/> reads it.
  /// </summary>
  public const string Bot = "$bot";
}

/// <summary>
/// Standard dimension names produced by the engine.
/// </summary>
public static class StandardDimensions
{
  /// <summary>The URL path of the page.</summary>
  public const string Page = "page";

  /// <summary>The host name of the page URL.</summary>
  public const string Hostname = "hostname";

  /// <summary>The first page of the session.</summary>
  public const string EntryPage = "entry_page";

  /// <summary>The traffic source, for example "Google", "Twitter", "news.example.com" or "Direct".</summary>
  public const string Source = "source";

  /// <summary>The referrer host name. Empty for direct traffic.</summary>
  public const string Referrer = "referrer";

  /// <summary>The acquisition channel, see <see cref="Channels"/>.</summary>
  public const string Channel = "channel";

  /// <summary>The utm_source URL parameter.</summary>
  public const string UtmSource = "utm_source";

  /// <summary>The utm_medium URL parameter.</summary>
  public const string UtmMedium = "utm_medium";

  /// <summary>The utm_campaign URL parameter.</summary>
  public const string UtmCampaign = "utm_campaign";

  /// <summary>The utm_term URL parameter.</summary>
  public const string UtmTerm = "utm_term";

  /// <summary>The utm_content URL parameter.</summary>
  public const string UtmContent = "utm_content";

  /// <summary>The country, as provided by the geolocator. ISO 3166-1 alpha-2 codes are recommended.</summary>
  public const string Country = "country";

  /// <summary>The region, as provided by the geolocator.</summary>
  public const string Region = "region";

  /// <summary>The city, as provided by the geolocator.</summary>
  public const string City = "city";

  /// <summary>The browser name.</summary>
  public const string Browser = "browser";

  /// <summary>The browser major version.</summary>
  public const string BrowserVersion = "browser_version";

  /// <summary>The operating system name.</summary>
  public const string OperatingSystem = "os";

  /// <summary>The operating system version.</summary>
  public const string OperatingSystemVersion = "os_version";

  /// <summary>The device type: "Desktop", "Mobile" or "Tablet".</summary>
  public const string Device = "device";

  /// <summary>The bot name, on <see cref="StandardEvents.Bot"/> events.</summary>
  public const string Bot = "bot";

  /// <summary>The bot category, on <see cref="StandardEvents.Bot"/> events. See <see cref="BotCategories"/>.</summary>
  public const string BotCategory = "bot_category";

  /// <summary>The original event name, on <see cref="StandardEvents.Bot"/> events.</summary>
  public const string Event = "event";

  /// <summary>
  /// Dimensions of <see cref="StandardEvents.Bot"/> events.
  /// </summary>
  public static readonly IReadOnlyList<string> BotLevel = [Page, Hostname, Event, Bot, BotCategory];

  /// <summary>
  /// Session-level dimensions. They are captured from the first event of a
  /// session and attached to every event of that session.
  /// </summary>
  public static readonly IReadOnlyList<string> SessionLevel =
  [
    Source,
    Referrer,
    Channel,
    UtmSource,
    UtmMedium,
    UtmCampaign,
    UtmTerm,
    UtmContent,
    Country,
    Region,
    City,
    Browser,
    BrowserVersion,
    OperatingSystem,
    OperatingSystemVersion,
    Device
  ];
}

/// <summary>
/// Acquisition channels assigned to sessions.
/// </summary>
public static class Channels
{
  /// <summary>No referrer and no campaign parameters.</summary>
  public const string Direct = "Direct";

  /// <summary>Unpaid search engine traffic.</summary>
  public const string OrganicSearch = "Organic Search";

  /// <summary>Paid search campaigns.</summary>
  public const string PaidSearch = "Paid Search";

  /// <summary>Unpaid social network traffic.</summary>
  public const string OrganicSocial = "Organic Social";

  /// <summary>Paid social campaigns.</summary>
  public const string PaidSocial = "Paid Social";

  /// <summary>Other paid campaigns.</summary>
  public const string PaidOther = "Paid Other";

  /// <summary>Email clients and newsletters.</summary>
  public const string Email = "Email";

  /// <summary>Affiliate campaigns.</summary>
  public const string Affiliates = "Affiliates";

  /// <summary>AI assistants and answer engines.</summary>
  public const string AIAssistants = "AI Assistants";

  /// <summary>Video platforms.</summary>
  public const string Video = "Video";

  /// <summary>Links from other websites.</summary>
  public const string Referral = "Referral";
}

/// <summary>
/// Bot categories of the built-in detector. Custom detectors may use their own.
/// </summary>
public static class BotCategories
{
  /// <summary>Search engine crawlers.</summary>
  public const string Search = "Search";

  /// <summary>AI crawlers and assistants fetching pages.</summary>
  public const string AI = "AI";

  /// <summary>SEO and marketing tools.</summary>
  public const string Seo = "SEO";

  /// <summary>Link preview services of social networks and messengers.</summary>
  public const string SocialPreview = "Social Preview";

  /// <summary>Uptime monitors and performance tools.</summary>
  public const string Monitoring = "Monitoring";

  /// <summary>Headless browsers and browser automation.</summary>
  public const string Automation = "Automation";

  /// <summary>HTTP libraries, command-line tools and scripts.</summary>
  public const string Scripts = "Scripts";

  /// <summary>Web archives.</summary>
  public const string Archiver = "Archiver";

  /// <summary>Other self-identified bots.</summary>
  public const string Other = "Other";
}

/// <summary>
/// The values the engine tracks on its own events, such as session duration.
/// Their names start with '$'.
/// </summary>
static class StandardMetrics
{
  /// <summary>
  /// Number of bounced sessions, on <see cref="StandardEvents.Session"/> rows.
  /// A session bounces when it has at most one interactive event.
  /// </summary>
  public const string Bounces = "$bounces";

  /// <summary>
  /// Total session duration in seconds, on <see cref="StandardEvents.Session"/> rows.
  /// </summary>
  public const string Duration = "$duration";

  /// <summary>
  /// Number of page views inside sessions, on <see cref="StandardEvents.Session"/> rows.
  /// </summary>
  public const string SessionPageViews = "$pageviews";

  /// <summary>
  /// Number of sessions that started on a page, on <see cref="StandardEvents.PageView"/> rows.
  /// </summary>
  public const string Entrances = "$entrances";

  /// <summary>
  /// Number of sessions that ended on a page, on <see cref="StandardEvents.PageView"/> rows.
  /// </summary>
  public const string Exits = "$exits";
}
