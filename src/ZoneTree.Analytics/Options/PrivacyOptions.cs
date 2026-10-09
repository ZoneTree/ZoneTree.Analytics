using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics;

/// <summary>
/// Privacy options.
/// </summary>
public sealed class PrivacyOptions
{
  /// <summary>
  /// Visitor identification strategy. Default is <see cref="VisitorIdentification.DailyRotatingHash"/>.
  /// </summary>
  public VisitorIdentification VisitorIdentification { get; set; } =
      VisitorIdentification.DailyRotatingHash;

  /// <summary>
  /// The secret key (at least 16 bytes) used to hash explicit visitor ids. When null,
  /// a random secret is generated and stored in the data directory. Supply it from a
  /// secret store to keep it away from the data files.
  /// </summary>
  public byte[] VisitorIdSecret { get; set; }

  /// <summary>
  /// Geolocation precision. Default is <see cref="GeoPrecision.City"/>.
  /// </summary>
  public GeoPrecision GeoPrecision { get; set; } = GeoPrecision.City;

  /// <summary>
  /// Derives browser, OS and device dimensions from the user agent. Default is true.
  /// </summary>
  public bool CollectUserAgentDetails { get; set; } = true;

  /// <summary>
  /// Also stores browser and OS versions. Default is false.
  /// </summary>
  public bool CollectVersions { get; set; }

  /// <summary>
  /// Keeps the query string in the page dimension. Query strings often contain
  /// personal data, so the default is false. UTM parameters are always extracted.
  /// </summary>
  public bool IncludeQueryString { get; set; }

  /// <summary>
  /// When set, only these custom dimensions are kept. Null keeps all.
  /// </summary>
  public ISet<string> AllowedDimensions { get; set; }

  /// <summary>
  /// Custom dimensions that are always discarded.
  /// </summary>
  public ISet<string> DeniedDimensions { get; set; }

  /// <summary>
  /// Maximum number of custom dimensions kept per event. Default is 32.
  /// </summary>
  public int MaxCustomDimensions { get; set; } = 32;

  /// <summary>
  /// Maximum number of custom metrics (numeric values) kept per event. Default is 32.
  /// </summary>
  public int MaxCustomMetrics { get; set; } = 32;

  /// <summary>
  /// Maximum length of dimension values. Longer values are truncated. Default is 512.
  /// Names (events, sites, dimensions and metrics) are limited to 128 characters.
  /// </summary>
  public int MaxValueLength { get; set; } = 512;

  /// <summary>
  /// True when <see cref="AllowedDimensions"/> and <see cref="DeniedDimensions"/> keep a custom dimension.
  /// </summary>
  internal bool IsCustomDimensionAllowed(string name) =>
      DeniedDimensions?.Contains(name) != true &&
      (AllowedDimensions == null || AllowedDimensions.Contains(name));

  internal PrivacyOptions Clone()
  {
    var clone = (PrivacyOptions)MemberwiseClone();
    clone.VisitorIdSecret = (byte[])VisitorIdSecret?.Clone();
    clone.AllowedDimensions = Copies.Of(AllowedDimensions);
    clone.DeniedDimensions = Copies.Of(DeniedDimensions);
    return clone;
  }

  internal void Validate()
  {
    if (!Enum.IsDefined(VisitorIdentification))
      throw new ArgumentException("Privacy.VisitorIdentification is not a valid value.");
    if (!Enum.IsDefined(GeoPrecision))
      throw new ArgumentException("Privacy.GeoPrecision is not a valid value.");
    if (VisitorIdSecret != null && VisitorIdSecret.Length < 16)
      throw new ArgumentException("Privacy.VisitorIdSecret must be at least 16 bytes.");
    if (MaxCustomDimensions < 0)
      throw new ArgumentException("Privacy.MaxCustomDimensions cannot be negative.");
    if (MaxCustomMetrics < 0)
      throw new ArgumentException("Privacy.MaxCustomMetrics cannot be negative.");
    if (MaxValueLength < 16)
      throw new ArgumentException("Privacy.MaxValueLength must be at least 16.");
  }
}

/// <summary>
/// How visitors are identified.
/// </summary>
public enum VisitorIdentification
{
  /// <summary>
  /// Explicit visitor ids are hashed with a persistent secret. Anonymous visitors are
  /// identified by a hash of site, IP address and user agent with a random salt that
  /// rotates every UTC day and is then destroyed. No cookies are required and visitors
  /// cannot be followed across days. This is the default.
  /// </summary>
  DailyRotatingHash,

  /// <summary>
  /// Like <see cref="DailyRotatingHash"/> but the anonymous hash uses a persistent secret,
  /// so anonymous visitors are recognized across days. Less private.
  /// </summary>
  PersistentHash,

  /// <summary>
  /// Only explicit <see cref="AnalyticsEvent.VisitorId"/> values identify visitors.
  /// </summary>
  ExplicitOnly,

  /// <summary>
  /// Visitors are not identified. Unique visitors and sessions are not available.
  /// </summary>
  None,
}

/// <summary>
/// Geolocation precision to keep.
/// </summary>
public enum GeoPrecision
{
  /// <summary>No geography is stored.</summary>
  None,

  /// <summary>Country only.</summary>
  Country,

  /// <summary>Country and region.</summary>
  Region,

  /// <summary>Country, region and city.</summary>
  City,
}
