namespace ZoneTree.Analytics;

/// <summary>
/// Where IP location databases come from and how they are kept current.
/// </summary>
/// <remarks>
/// With sources and no <see cref="WebOptions.GeoLocator"/>, the engine runs an
/// <see cref="IpLocationProvider"/>: it downloads missing databases, checks for newer ones and
/// keeps locations to <see cref="PrivacyOptions.GeoPrecision"/>.
/// </remarks>
public sealed class IpLocationOptions
{
  /// <summary>
  /// The database files: one file with IPv4 and IPv6, or separate files. Empty by default: no
  /// IP geolocation. <see cref="IpLocationSources"/> has ready-made sources.
  /// </summary>
  public List<IpLocationSource> Sources { get; private set; } = [];

  /// <summary>
  /// The directory of database files whose source has no absolute path. A relative directory is
  /// resolved against the data directory. Default is "ip-location".
  /// </summary>
  public string Directory { get; set; } = "ip-location";

  /// <summary>
  /// Downloads missing database files that have a URL. Default is true. Until they are
  /// downloaded, addresses are not located.
  /// </summary>
  public bool DownloadMissing { get; set; } = true;

  /// <summary>
  /// How often sources with a URL are checked for a newer database. A check is a conditional
  /// request that downloads nothing when the database did not change. The time of the last check
  /// is the file's modification time, so restarts do not check again early. At least one day, so
  /// database hosts are not checked more often than they publish. Null disables updates. Default
  /// is one day.
  /// </summary>
  public TimeSpan? UpdateInterval { get; set; } = TimeSpan.FromDays(1);

  /// <summary>
  /// How long to wait before trying a failed download or load again. At least one day, so a
  /// failing source is not requested over and over. <see cref="IpLocationProvider.UpdateAsync"/>
  /// tries again at once. Default is one day.
  /// </summary>
  public TimeSpan RetryInterval { get; set; } = TimeSpan.FromDays(1);

  /// <summary>
  /// How long one download may take. Default is ten minutes.
  /// </summary>
  public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

  /// <summary>
  /// The largest database file downloaded, in bytes. Default is 1 GiB.
  /// </summary>
  public long MaxDownloadBytes { get; set; } = 1L << 30;

  /// <summary>
  /// The client for downloads, for example one with a proxy. Default is null: the provider
  /// creates and disposes its own.
  /// </summary>
  public HttpClient HttpClient { get; set; }

  internal IpLocationOptions Clone()
  {
    var clone = (IpLocationOptions)MemberwiseClone();
    clone.Sources = [.. Sources];
    return clone;
  }

  internal void Validate()
  {
    foreach (var source in Sources)
    {
      if (source == null)
        throw new ArgumentException("Web.IpLocation.Sources contains an empty entry.");
      source.Validate();
    }
    var duplicate = Sources.GroupBy(source => source.FileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
    if (duplicate != null)
      throw new ArgumentException($"Web.IpLocation.Sources save two databases as {duplicate.Key}. Give them different paths.");
    if (Sources.Count > 0 && string.IsNullOrWhiteSpace(Directory))
      throw new ArgumentException("Web.IpLocation.Directory is required.");
    if (UpdateInterval is { } interval && interval < TimeSpan.FromDays(1))
      throw new ArgumentException("Web.IpLocation.UpdateInterval must be at least one day.");
    if (RetryInterval < TimeSpan.FromDays(1))
      throw new ArgumentException("Web.IpLocation.RetryInterval must be at least one day.");
    if (DownloadTimeout <= TimeSpan.Zero)
      throw new ArgumentException("Web.IpLocation.DownloadTimeout must be positive.");
    if (MaxDownloadBytes < 1024)
      throw new ArgumentException("Web.IpLocation.MaxDownloadBytes must be at least 1024.");
  }

  // The full path of each source's file.
  internal string[] ResolvePaths(string baseDirectory)
  {
    var directory = Path.Combine(baseDirectory ?? Environment.CurrentDirectory, Directory);
    var paths = Sources.Select(source => Path.GetFullPath(Path.Combine(directory, source.FileName))).ToArray();
    var duplicate = paths.GroupBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
    if (duplicate != null)
      throw new ArgumentException($"Web.IpLocation.Sources save two databases to {duplicate.Key}. Give them different paths.");
    return paths;
  }
}

/// <summary>
/// An IP location database file.
/// </summary>
/// <param name="Url">Where the file is downloaded from, over HTTP or HTTPS. Null for a file that
/// is maintained by other means.</param>
/// <param name="Path">The file. A relative path is resolved against
/// <see cref="IpLocationOptions.Directory"/>. Default is the URL's file name.</param>
public sealed record IpLocationSource(string Url, string Path = null)
{
  internal string FileName => Path ?? (Uri.TryCreate(Url, UriKind.Absolute, out var uri)
      ? Uri.UnescapeDataString(uri.Segments[^1])
      : null);

  internal void Validate()
  {
    if (Url == null && string.IsNullOrWhiteSpace(Path))
      throw new ArgumentException("An IP location source needs a URL or a path.");
    if (Url != null && (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
      throw new ArgumentException($"The IP location source URL {Url} is not an absolute HTTP or HTTPS URL.");
    var name = FileName;
    if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
      throw new ArgumentException($"The IP location source {Url ?? Path} has no file name. Set its path.");
    // A name from a URL stays in the directory: an escaped "../" in the URL does not leave it.
    if (Path == null && (name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0))
      throw new ArgumentException($"The IP location source URL {Url} does not end with a file name. Set its path.");
  }
}

/// <summary>
/// Ready-made IP location database sources from
/// <see href="https://github.com/sapics/ip-location-db">ip-location-db</see>.
/// </summary>
public static class IpLocationSources
{
  const string Releases = "https://github.com/sapics/ip-location-db/releases/download/latest/";

  /// <summary>
  /// Countries, updated daily, under the PDDL: free to use without attribution. It prefers the
  /// country of the end user over that of the VPN or proxy server the user connects through.
  /// One file of about 8 MB.
  /// </summary>
  public static IReadOnlyList<IpLocationSource> UserCountry { get; } = [new(Releases + "user-country.mmdb")];

  /// <summary>
  /// Countries, regions and cities from DB-IP Lite, updated monthly, under CC BY 4.0: show
  /// "IP Geolocation by DB-IP" with a link to https://db-ip.com where locations are shown.
  /// Separate IPv4 and IPv6 files.
  /// </summary>
  public static IReadOnlyList<IpLocationSource> DbIpCity { get; } =
      [new(Releases + "dbip-city-ipv4.mmdb"), new(Releases + "dbip-city-ipv6.mmdb")];
}
