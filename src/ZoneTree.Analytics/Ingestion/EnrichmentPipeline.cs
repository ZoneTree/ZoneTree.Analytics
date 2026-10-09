using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Privacy;
using ZoneTree.Analytics.Web;
using ZoneTree.Logger;

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
  /// True for <see cref="StandardEvents.Bot"/> events.
  /// </summary>
  public bool IsBot;

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
  /// <see cref="SessionSchema.Names"/>. Null entries are not collected.
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
  Bot,
  FutureTimestamp,
}

/// <summary>
/// Validates, sanitizes and enriches incoming events. Thread-safe.
/// </summary>
/// <remarks>
/// Extensions (user agent parser, bot detectors, geolocator, enrichers) fail open: an exception
/// is logged and counted, and the event is processed as if the extension had not been there.
/// </remarks>
sealed class EnrichmentPipeline
{
  static readonly int SourceIndex = SessionSchema.Standard.IndexOf(StandardDimensions.Source);
  static readonly int ReferrerIndex = SessionSchema.Standard.IndexOf(StandardDimensions.Referrer);
  static readonly int ChannelIndex = SessionSchema.Standard.IndexOf(StandardDimensions.Channel);
  static readonly int UtmSourceIndex = SessionSchema.Standard.IndexOf(StandardDimensions.UtmSource);
  static readonly int UtmMediumIndex = SessionSchema.Standard.IndexOf(StandardDimensions.UtmMedium);
  static readonly int UtmCampaignIndex = SessionSchema.Standard.IndexOf(StandardDimensions.UtmCampaign);
  static readonly int UtmTermIndex = SessionSchema.Standard.IndexOf(StandardDimensions.UtmTerm);
  static readonly int UtmContentIndex = SessionSchema.Standard.IndexOf(StandardDimensions.UtmContent);
  static readonly int CountryIndex = SessionSchema.Standard.IndexOf(StandardDimensions.Country);
  static readonly int RegionIndex = SessionSchema.Standard.IndexOf(StandardDimensions.Region);
  static readonly int CityIndex = SessionSchema.Standard.IndexOf(StandardDimensions.City);
  static readonly int BrowserIndex = SessionSchema.Standard.IndexOf(StandardDimensions.Browser);
  static readonly int BrowserVersionIndex = SessionSchema.Standard.IndexOf(StandardDimensions.BrowserVersion);
  static readonly int OperatingSystemIndex = SessionSchema.Standard.IndexOf(StandardDimensions.OperatingSystem);
  static readonly int OperatingSystemVersionIndex = SessionSchema.Standard.IndexOf(StandardDimensions.OperatingSystemVersion);
  static readonly int DeviceIndex = SessionSchema.Standard.IndexOf(StandardDimensions.Device);

  readonly AnalyticsOptions options;

  readonly VisitorHasher visitorHasher;

  readonly ReferrerClassifier referrerClassifier;

  readonly ILogger logger;

  readonly Counters counters;

  public EnrichmentPipeline(AnalyticsOptions options, VisitorHasher visitorHasher, ILogger logger, Counters counters)
  {
    this.options = options;
    this.visitorHasher = visitorHasher;
    this.logger = logger;
    this.counters = counters;
    referrerClassifier = new ReferrerClassifier(options.Web);
    Schema = new SessionSchema(options.Sessions.Dimensions);
  }

  /// <summary>
  /// The session-level dimensions of the engine, standard and custom.
  /// </summary>
  public SessionSchema Schema { get; }

  public string NormalizeSite(string site)
  {
    var normalized = Text.Name(site);
    return normalized.Length == 0 ? options.DefaultSite : normalized;
  }

  /// <summary>
  /// Logs and counts an exception thrown by an extension.
  /// </summary>
  void ExtensionFailed(string extension, Exception exception)
  {
    Interlocked.Increment(ref counters.Errors);
    logger.LogError(new InvalidOperationException($"The {extension} threw an exception; the event is processed without it.", exception));
  }

  public EnrichedEvent Enrich(EventSnapshot e, out RejectReason reason)
  {
    var privacy = options.Privacy;
    var name = Text.Name(e.Name);
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

    var site = NormalizeSite(e.Site);
    UserAgentInfo userAgent = null;
    if (!string.IsNullOrWhiteSpace(e.UserAgent) && options.Web.UserAgentParser != null)
    {
      try
      {
        userAgent = options.Web.UserAgentParser.Parse(e.UserAgent);
      }
      catch (Exception exception)
      {
        ExtensionFailed("user agent parser", exception);
      }
    }

    var bot = DetectBot(e, name, site, ticks, userAgent);
    if (bot != null)
    {
      if (options.Web.Bots == BotTraffic.Exclude)
      {
        reason = RejectReason.Bot;
        return null;
      }
      var botEvent = BuildBotEvent(e, name, site, ticks, bot);
      RunEnrichers(e, name, site, ticks, userAgent, bot, botEvent);
      reason = RejectReason.None;
      return botEvent;
    }

    var result = new EnrichedEvent
    {
      Site = site,
      Name = name,
      Ticks = ticks,
      IsPageView = name == StandardEvents.PageView,
      // Sized for the session-level dimensions and the entry page the worker adds.
      Dimensions = new(Schema.Count + (e.Dimensions?.Length ?? 0) + 3, StringComparer.Ordinal),
      HasWebContext =
          !string.IsNullOrWhiteSpace(e.Url) ||
          !string.IsNullOrWhiteSpace(e.Referrer) ||
          !string.IsNullOrWhiteSpace(e.UserAgent) ||
          !string.IsNullOrWhiteSpace(e.IpAddress)
    };
    result.Interactive = result.IsPageView || !e.NonInteractive;

    // Custom dimensions. Session-level names, standard or custom, go to the session dimensions.
    string[] sessionOverrides = null;
    if (e.Dimensions != null)
    {
      var customCount = 0;
      foreach (var (rawKey, rawValue) in e.Dimensions)
      {
        if (rawValue == null)
          continue;
        var key = Text.Name(rawKey);
        if (key.Length == 0 || key.Contains(',') || key == StandardDimensions.EntryPage)
          continue;
        var value = Text.Value(rawValue, privacy.MaxValueLength);
        var sessionIndex = Schema.IndexOf(key);
        if (sessionIndex >= 0)
        {
          sessionOverrides ??= new string[Schema.Count];
          sessionOverrides[sessionIndex] = value;
          continue;
        }
        if (key is StandardDimensions.Page or StandardDimensions.Hostname)
        {
          result.Dimensions[key] = value;
          continue;
        }
        if (!privacy.IsCustomDimensionAllowed(key) || customCount >= privacy.MaxCustomDimensions)
          continue;
        result.Dimensions[key] = value;
        ++customCount;
      }
    }

    // Page.
    ParsedUrl url = null;
    if (!string.IsNullOrWhiteSpace(e.Url))
    {
      url = UrlParser.Parse(e.Url, options.Web, privacy.IncludeQueryString);
      result.Dimensions.TryAdd(StandardDimensions.Page, Text.Value(url.Path, privacy.MaxValueLength));
      result.Dimensions.TryAdd(StandardDimensions.Hostname, Text.Value(url.Hostname, privacy.MaxValueLength));
    }

    // Metrics.
    if (e.Metrics != null && e.Metrics.Length > 0)
    {
      result.Metrics = new(Math.Min(e.Metrics.Length, privacy.MaxCustomMetrics));
      foreach (var (rawKey, value) in e.Metrics)
      {
        if (result.Metrics.Count >= privacy.MaxCustomMetrics)
          break;
        if (!double.IsFinite(value))
          continue;
        var key = Text.Name(rawKey);
        if (key.Length == 0 || key[0] == '$')
          continue;
        result.Metrics.Add(new(key, value));
      }
    }

    if (result.HasWebContext || sessionOverrides != null)
    {
      var pageHost = result.Dimensions.GetValueOrDefault(StandardDimensions.Hostname) ?? "";
      result.SessionDimensions = BuildSessionDimensions(e, url, pageHost, userAgent, sessionOverrides);
    }

    RunEnrichers(e, name, site, ticks, userAgent, null, result);

    result.Visitor = visitorHasher.Compute(site, e.VisitorId, e.IpAddress, e.UserAgent, ticks);
    reason = RejectReason.None;
    return result;
  }

  static EventContext FillContext(
      EventContext context, EventSnapshot e, string name, string site, long ticks, UserAgentInfo userAgent)
  {
    context.EventName = name;
    context.Timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
    context.Site = site;
    context.Url = e.Url;
    context.Referrer = e.Referrer;
    context.IpAddress = e.IpAddress;
    context.UserAgent = e.UserAgent;
    context.UserAgentInfo = userAgent;
    context.TrackedDimensionSource = e.Dimensions;
    return context;
  }

  BotInfo DetectBot(EventSnapshot e, string name, string site, long ticks, UserAgentInfo userAgent)
  {
    var detectors = options.Web.BotDetectors;
    if (detectors == null || detectors.Count == 0)
      return null;
    EventContext context = null;
    foreach (var detector in detectors)
    {
      if (detector == null)
        continue;
      try
      {
        context ??= FillContext(new EventContext(), e, name, site, ticks, userAgent);
        var bot = detector.Detect(context);
        if (bot != null)
          return bot;
      }
      catch (Exception exception)
      {
        ExtensionFailed("bot detector " + detector.GetType().Name, exception);
      }
    }
    return null;
  }

  /// <summary>
  /// A bot hit: the page, hostname, original event name, bot name and category. No visitor,
  /// no session and no other dimensions, so bot traffic never affects human metrics.
  /// </summary>
  EnrichedEvent BuildBotEvent(EventSnapshot e, string name, string site, long ticks, BotInfo bot)
  {
    var maxLength = options.Privacy.MaxValueLength;
    var result = new EnrichedEvent
    {
      Site = site,
      Name = StandardEvents.Bot,
      Ticks = ticks,
      IsBot = true,
      Dimensions = new(StringComparer.Ordinal)
      {
        [StandardDimensions.Event] = name,
        [StandardDimensions.Bot] = Text.Value(bot.Name, maxLength),
        [StandardDimensions.BotCategory] = Text.Value(bot.Category, maxLength)
      }
    };
    if (!string.IsNullOrWhiteSpace(e.Url))
    {
      var url = UrlParser.Parse(e.Url, options.Web, options.Privacy.IncludeQueryString);
      result.Dimensions[StandardDimensions.Page] = Text.Value(url.Path, maxLength);
      result.Dimensions[StandardDimensions.Hostname] = Text.Value(url.Hostname, maxLength);
    }
    result.Page = result.Dimensions.GetValueOrDefault(StandardDimensions.Page);
    result.Hostname = result.Dimensions.GetValueOrDefault(StandardDimensions.Hostname) ?? "";
    return result;
  }

  /// <summary>
  /// Runs the configured enrichers, then derives the page, hostname and custom-dimension flag
  /// from the final dimensions.
  /// </summary>
  void RunEnrichers(
      EventSnapshot e, string name, string site, long ticks, UserAgentInfo userAgent, BotInfo bot, EnrichedEvent result)
  {
    var enrichers = options.Enrichers;
    if (enrichers != null && enrichers.Count > 0)
    {
      var context = (EnrichmentContext)FillContext(new EnrichmentContext(), e, name, site, ticks, userAgent);
      context.Bot = bot;
      context.Getter = dimension => GetDimension(result, dimension);
      context.Setter = (dimension, value) => SetDimension(result, dimension, value);
      foreach (var enricher in enrichers)
      {
        if (enricher == null)
          continue;
        try
        {
          enricher.Enrich(context);
        }
        catch (Exception exception)
        {
          ExtensionFailed("enricher " + enricher.GetType().Name, exception);
        }
      }
    }
    result.Page = result.Dimensions.GetValueOrDefault(StandardDimensions.Page);
    result.Hostname = result.Dimensions.GetValueOrDefault(StandardDimensions.Hostname) ?? "";
    if (!result.IsBot)
    {
      result.HasCustomDimensions = result.Dimensions.Keys.Any(
          x => x is not (StandardDimensions.Page or StandardDimensions.Hostname));
    }
  }

  string GetDimension(EnrichedEvent result, string dimension)
  {
    if (string.IsNullOrEmpty(dimension))
      return null;
    var index = result.IsBot ? -1 : Schema.IndexOf(dimension);
    if (index >= 0)
      return result.SessionDimensions?[index];
    return result.Dimensions.GetValueOrDefault(dimension);
  }

  void SetDimension(EnrichedEvent result, string dimension, string value)
  {
    var name = Text.Name(dimension);
    if (name.Length == 0 || name.Contains(','))
      throw new ArgumentException($"'{dimension}' is not a valid dimension name.", nameof(dimension));
    if (name == StandardDimensions.EntryPage)
      throw new ArgumentException("The entry page is maintained by the engine; set the page instead.", nameof(dimension));
    if (result.IsBot && name is StandardDimensions.Bot or StandardDimensions.BotCategory or StandardDimensions.Event)
      throw new ArgumentException($"'{name}' is maintained by the engine on bot events.", nameof(dimension));
    var sanitized = value == null ? null : Text.Value(value, options.Privacy.MaxValueLength);
    var index = result.IsBot ? -1 : Schema.IndexOf(name);
    if (index >= 0)
    {
      result.SessionDimensions ??= new string[Schema.Count];
      result.SessionDimensions[index] = sanitized;
      return;
    }
    if (sanitized == null)
      result.Dimensions.Remove(name);
    else
      result.Dimensions[name] = sanitized;
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
    var values = new string[Schema.Count];

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
        catch (Exception exception)
        {
          ExtensionFailed("geolocator", exception);
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

    // Custom session-level dimensions come from the event only.
    for (var i = SessionSchema.StandardCount; i < values.Length; ++i)
      values[i] = overrides?[i];
    return values;
  }
}
