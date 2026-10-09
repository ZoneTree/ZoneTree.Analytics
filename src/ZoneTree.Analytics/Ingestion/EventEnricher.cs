using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Privacy;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.Ingestion;

/// <summary>
/// An event after validation, sanitization and enrichment.
/// </summary>
sealed class EnrichedEvent
{
  public string Site;

  public string Name;

  public long Ticks;

  public ulong Visitor;

  public bool Interactive;

  public bool IsPageView;

  /// <summary>
  /// True when the event carries web context (URL, referrer, user agent or IP address).
  /// </summary>
  public bool HasWebContext;

  /// <summary>
  /// Event-level dimensions: custom dimensions plus page and hostname.
  /// </summary>
  public Dictionary<string, string> Dimensions;

  /// <summary>
  /// Session-level dimensions computed from this event, aligned with
  /// <see cref="StandardDimensions.SessionLevel"/>. Null entries are not collected.
  /// </summary>
  public string[] SessionDimensions;

  public List<KeyValuePair<string, double>> Metrics;

  /// <summary>
  /// True when the event carries custom dimensions besides page and hostname.
  /// </summary>
  public bool HasCustomDimensions;

  /// <summary>
  /// The page path, or null when the event has no page.
  /// </summary>
  public string Page;

  public string Hostname;
}

enum RejectReason
{
  None,
  Invalid,
  Filtered,
  Bot,
  FutureTimestamp,
}

/// <summary>
/// Validates, sanitizes and enriches incoming events. Thread-safe.
/// </summary>
sealed class EventEnricher
{
  const int MaxNameLength = 128;

  const int MaxMetrics = 32;

  static readonly Dictionary<string, int> SessionLevelIndex = StandardDimensions.SessionLevel
      .Select((name, index) => (name, index))
      .ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);

  static readonly int SourceIndex = SessionLevelIndex[StandardDimensions.Source];
  static readonly int ReferrerIndex = SessionLevelIndex[StandardDimensions.Referrer];
  static readonly int ChannelIndex = SessionLevelIndex[StandardDimensions.Channel];
  static readonly int UtmSourceIndex = SessionLevelIndex[StandardDimensions.UtmSource];
  static readonly int UtmMediumIndex = SessionLevelIndex[StandardDimensions.UtmMedium];
  static readonly int UtmCampaignIndex = SessionLevelIndex[StandardDimensions.UtmCampaign];
  static readonly int UtmTermIndex = SessionLevelIndex[StandardDimensions.UtmTerm];
  static readonly int UtmContentIndex = SessionLevelIndex[StandardDimensions.UtmContent];
  static readonly int CountryIndex = SessionLevelIndex[StandardDimensions.Country];
  static readonly int RegionIndex = SessionLevelIndex[StandardDimensions.Region];
  static readonly int CityIndex = SessionLevelIndex[StandardDimensions.City];
  static readonly int BrowserIndex = SessionLevelIndex[StandardDimensions.Browser];
  static readonly int BrowserVersionIndex = SessionLevelIndex[StandardDimensions.BrowserVersion];
  static readonly int OperatingSystemIndex = SessionLevelIndex[StandardDimensions.OperatingSystem];
  static readonly int OperatingSystemVersionIndex = SessionLevelIndex[StandardDimensions.OperatingSystemVersion];
  static readonly int DeviceIndex = SessionLevelIndex[StandardDimensions.Device];

  readonly AnalyticsOptions options;

  readonly VisitorHasher visitorHasher;

  readonly ReferrerClassifier referrerClassifier;

  readonly UserAgentParser userAgentParser = UserAgentParser.Shared;

  public EventEnricher(AnalyticsOptions options, VisitorHasher visitorHasher)
  {
    this.options = options;
    this.visitorHasher = visitorHasher;
    referrerClassifier = new ReferrerClassifier(options.Web);
  }

  public static bool IsSessionLevel(string dimension) => SessionLevelIndex.ContainsKey(dimension);

  public static int SessionLevelIndexOf(string dimension) =>
      SessionLevelIndex.TryGetValue(dimension, out var index) ? index : -1;

  public string NormalizeSite(string site)
  {
    var normalized = Text.Name(site, MaxNameLength);
    return normalized.Length == 0 ? options.DefaultSite : normalized;
  }

  public EnrichedEvent Enrich(EventSnapshot e, out RejectReason reason)
  {
    var privacy = options.Privacy;
    var name = Text.Name(e.Name, MaxNameLength);
    if (name.Length == 0 || name[0] == '$')
    {
      reason = RejectReason.Invalid;
      return null;
    }

    var ticks = e.UtcTicks;
    if (ticks > e.ReceivedUtcTicks + options.Ingestion.MaxFutureSkew.Ticks)
    {
      reason = RejectReason.FutureTimestamp;
      return null;
    }

    UserAgentInfo userAgent = null;
    if (!string.IsNullOrWhiteSpace(e.UserAgent))
    {
      userAgent = userAgentParser.Parse(e.UserAgent);
      if (userAgent.IsBot && options.Web.ExcludeBots)
      {
        reason = RejectReason.Bot;
        return null;
      }
    }

    var site = NormalizeSite(e.Site);
    var result = new EnrichedEvent
    {
      Site = site,
      Name = name,
      Ticks = ticks,
      IsPageView = name == StandardEvents.PageView,
      Dimensions = new(StringComparer.Ordinal),
      HasWebContext =
          !string.IsNullOrWhiteSpace(e.Url) ||
          !string.IsNullOrWhiteSpace(e.Referrer) ||
          !string.IsNullOrWhiteSpace(e.UserAgent) ||
          !string.IsNullOrWhiteSpace(e.IpAddress)
    };
    result.Interactive = result.IsPageView || !e.NonInteractive;

    // Custom dimensions. Standard session-level names are overrides.
    string[] sessionOverrides = null;
    if (e.Dimensions != null)
    {
      var customCount = 0;
      foreach (var (rawKey, rawValue) in e.Dimensions)
      {
        if (rawValue == null)
          continue;
        var key = Text.Name(rawKey, MaxNameLength);
        if (key.Length == 0 || key.Contains(',') || key == StandardDimensions.EntryPage)
          continue;
        var value = Text.Value(rawValue, privacy.MaxValueLength);
        var sessionIndex = SessionLevelIndexOf(key);
        if (sessionIndex >= 0)
        {
          sessionOverrides ??= new string[StandardDimensions.SessionLevel.Count];
          sessionOverrides[sessionIndex] = value;
          continue;
        }
        if (key is StandardDimensions.Page or StandardDimensions.Hostname)
        {
          result.Dimensions[key] = value;
          continue;
        }
        if (privacy.DeniedDimensions != null && privacy.DeniedDimensions.Contains(key))
          continue;
        if (privacy.AllowedDimensions != null && !privacy.AllowedDimensions.Contains(key))
          continue;
        if (customCount >= privacy.MaxCustomDimensions)
          continue;
        result.Dimensions[key] = value;
        ++customCount;
      }
      result.HasCustomDimensions = customCount > 0;
    }

    // Page.
    ParsedUrl url = null;
    if (!string.IsNullOrWhiteSpace(e.Url))
    {
      url = UrlParser.Parse(e.Url, options.Web, privacy.IncludeQueryString);
      result.Dimensions.TryAdd(StandardDimensions.Page, Text.Value(url.Path, privacy.MaxValueLength));
      result.Dimensions.TryAdd(StandardDimensions.Hostname, Text.Value(url.Hostname, privacy.MaxValueLength));
    }
    result.Page = result.Dimensions.GetValueOrDefault(StandardDimensions.Page);
    result.Hostname = result.Dimensions.GetValueOrDefault(StandardDimensions.Hostname) ?? "";

    // Metrics.
    if (e.Metrics != null && e.Metrics.Length > 0)
    {
      result.Metrics = new(Math.Min(e.Metrics.Length, MaxMetrics));
      foreach (var (rawKey, value) in e.Metrics)
      {
        if (result.Metrics.Count >= MaxMetrics)
          break;
        if (!double.IsFinite(value))
          continue;
        var key = Text.Name(rawKey, MaxNameLength);
        if (key.Length == 0 || key[0] == '$')
          continue;
        result.Metrics.Add(new(key, value));
      }
    }

    if (result.HasWebContext || sessionOverrides != null)
      result.SessionDimensions = BuildSessionDimensions(e, url, result.Hostname, userAgent, sessionOverrides);

    result.Visitor = visitorHasher.Compute(site, e.VisitorId, e.IpAddress, e.UserAgent, ticks);
    reason = RejectReason.None;
    return result;
  }

  string[] BuildSessionDimensions(
      EventSnapshot e,
      ParsedUrl url,
      string pageHost,
      UserAgentInfo userAgent,
      string[] overrides)
  {
    var privacy = options.Privacy;
    var maxLength = privacy.MaxValueLength;
    var values = new string[StandardDimensions.SessionLevel.Count];

    string Pick(int index, string computed) =>
        overrides?[index] ?? Text.Value(computed, maxLength);

    values[UtmSourceIndex] = Pick(UtmSourceIndex, url?.UtmSource);
    values[UtmMediumIndex] = Pick(UtmMediumIndex, url?.UtmMedium);
    values[UtmCampaignIndex] = Pick(UtmCampaignIndex, url?.UtmCampaign);
    values[UtmTermIndex] = Pick(UtmTermIndex, url?.UtmTerm);
    values[UtmContentIndex] = Pick(UtmContentIndex, url?.UtmContent);

    referrerClassifier.Classify(
        e.Referrer,
        pageHost,
        values[UtmSourceIndex],
        values[UtmMediumIndex],
        out var referrerHost,
        out var source,
        out var channel);
    values[ReferrerIndex] = Pick(ReferrerIndex, referrerHost);
    values[SourceIndex] = Pick(SourceIndex, source);
    values[ChannelIndex] = Pick(ChannelIndex, channel);

    if (privacy.GeoPrecision != GeoPrecision.None)
    {
      GeoLocation location = null;
      var needsLookup =
          overrides?[CountryIndex] == null ||
          (privacy.GeoPrecision >= GeoPrecision.Region && overrides[RegionIndex] == null) ||
          (privacy.GeoPrecision >= GeoPrecision.City && overrides[CityIndex] == null);
      if (needsLookup && options.Web.GeoLocator != null && !string.IsNullOrWhiteSpace(e.IpAddress))
      {
        try
        {
          location = options.Web.GeoLocator.Locate(e.IpAddress);
        }
        catch
        {
          // Geolocation is best effort.
        }
      }
      values[CountryIndex] = Pick(CountryIndex, location?.Country);
      if (privacy.GeoPrecision >= GeoPrecision.Region)
        values[RegionIndex] = Pick(RegionIndex, location?.Region);
      if (privacy.GeoPrecision >= GeoPrecision.City)
        values[CityIndex] = Pick(CityIndex, location?.City);
    }

    if (privacy.CollectUserAgentDetails)
    {
      values[BrowserIndex] = Pick(BrowserIndex, userAgent?.Browser);
      values[OperatingSystemIndex] = Pick(OperatingSystemIndex, userAgent?.OperatingSystem);
      values[DeviceIndex] = Pick(DeviceIndex, userAgent?.Device);
      if (privacy.CollectVersions)
      {
        values[BrowserVersionIndex] = Pick(BrowserVersionIndex, userAgent?.BrowserVersion);
        values[OperatingSystemVersionIndex] = Pick(OperatingSystemVersionIndex, userAgent?.OperatingSystemVersion);
      }
    }
    return values;
  }
}
