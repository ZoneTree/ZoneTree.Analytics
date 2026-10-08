namespace ZoneTree.Analytics.Web;

/// <summary>
/// Resolves an IP address to a location.
/// </summary>
/// <remarks>
/// Implementations are called from ingestion threads and must be thread-safe.
/// Return null when the location is unknown.
/// </remarks>
public interface IGeoLocator
{
  /// <summary>
  /// Resolves the location of an IP address.
  /// </summary>
  GeoLocation Locate(string ipAddress);
}

/// <summary>
/// A resolved location. Any part can be null.
/// </summary>
/// <param name="Country">The country. ISO 3166-1 alpha-2 codes are recommended.</param>
/// <param name="Region">The region or subdivision.</param>
/// <param name="City">The city.</param>
public sealed record GeoLocation(string Country, string Region = null, string City = null);
