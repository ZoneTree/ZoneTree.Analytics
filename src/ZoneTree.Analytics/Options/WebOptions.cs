using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics;

/// <summary>
/// Web enrichment options.
/// </summary>
public sealed class WebOptions
{
  /// <summary>
  /// Resolves IP addresses to locations. Default is null: no geolocation. The library ships
  /// no geolocation database; adapt one, or pass geography through event dimensions, for
  /// example from a CDN header.
  /// </summary>
  public IGeoLocator GeoLocator { get; set; }

  /// <summary>
  /// Parses user agents into browser, operating system and device.
  /// Default is <see cref="ZoneTree.Analytics.UserAgentParser.Default"/>.
  /// </summary>
  public IUserAgentParser UserAgentParser { get; set; } = ZoneTree.Analytics.UserAgentParser.Default;

  /// <summary>
  /// What happens to bot traffic. Default is <see cref="BotTraffic.Exclude"/>.
  /// </summary>
  public BotTraffic Bots { get; set; } = BotTraffic.Exclude;

  /// <summary>
  /// Bot detectors, in order; the first that recognizes a bot wins. Default is
  /// <see cref="BotDetector.UserAgent"/>. Add detectors (for example based on IP ranges) or
  /// clear the list to treat all traffic as human.
  /// </summary>
  public List<IBotDetector> BotDetectors { get; private set; } = [BotDetector.UserAgent];

  /// <summary>
  /// Removes a trailing slash from page paths (except the root). Default is true.
  /// </summary>
  public bool TrimTrailingSlash { get; set; } = true;

  /// <summary>
  /// Lowercases page paths. Default is false.
  /// </summary>
  public bool LowercasePaths { get; set; }

  /// <summary>
  /// Additional referrer host to source name mappings, for example
  /// "news.ycombinator.com" to "Hacker News". Hosts match exactly or as a parent domain.
  /// </summary>
  public IDictionary<string, string> ReferrerSources { get; set; }

  /// <summary>
  /// Hosts treated as internal. Referrers from these hosts, or from the page host itself,
  /// count as direct traffic.
  /// </summary>
  public ISet<string> InternalHosts { get; set; }

  internal WebOptions Clone()
  {
    var clone = (WebOptions)MemberwiseClone();
    clone.BotDetectors = BotDetectors == null ? [] : [.. BotDetectors];
    clone.ReferrerSources = Copies.Of(ReferrerSources);
    clone.InternalHosts = Copies.Of(InternalHosts);
    return clone;
  }
}

/// <summary>
/// What happens to bot traffic.
/// </summary>
public enum BotTraffic
{
  /// <summary>Bot events are discarded and counted in <see cref="AnalyticsStatistics.BotsExcluded"/>.</summary>
  Exclude,

  /// <summary>
  /// Bot events are recorded as <see cref="StandardEvents.Bot"/> events, separate from human
  /// traffic: they carry the page, hostname, original event name, bot name and bot category,
  /// and never a visitor or session, so human metrics are unaffected. Compare them with
  /// <see cref="Metric.BotHits"/>.
  /// </summary>
  TrackSeparately,
}
