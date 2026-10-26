using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ZoneTree.Analytics.Geo;

namespace ZoneTree.Analytics;

/// <summary>
/// Resolves IP addresses to countries from a MaxMind DB (MMDB) country database, for example
/// <see href="https://github.com/sapics/ip-location-db">ip-location-db</see> user-country,
/// DB-IP Lite Country or GeoLite2 Country. Countries are ISO 3166-1 alpha-2 codes.
/// </summary>
/// <remarks>
/// <para>
/// Loading flattens the database into sorted address ranges, so a lookup reads a prefix index and
/// binary searches a few ranges instead of walking the search tree bit by bit. Lookups do not
/// allocate and are thread-safe. IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) resolve as IPv4.
/// The user-country database, about 650,000 IPv4 and IPv6 ranges, takes about 9 MB of memory.
/// </para>
/// <para>
/// The library ships no database. Download one with <see cref="DownloadAsync"/>, for example
/// when the file is missing:
/// <code>
/// var path = "data/user-country.mmdb";
/// options.Web.GeoLocator = File.Exists(path)
///     ? IpCountryDatabase.Load(path)
///     : await IpCountryDatabase.DownloadAsync(path);
/// </code>
/// A loaded database does not change; load the file again to use a newer one.
/// </para>
/// </remarks>
public sealed class IpCountryDatabase : IGeoLocator
{
  /// <summary>
  /// The latest ip-location-db user-country database (PDDL, free use without attribution),
  /// updated daily. It prefers the country of the end user over that of the VPN or proxy server
  /// the user connects through.
  /// </summary>
  public const string DefaultUrl = "https://github.com/sapics/ip-location-db/releases/download/latest/user-country.mmdb";

  // Index 0 is unknown.
  readonly GeoLocation[] locations;

  readonly AddressRanges<uint> ipv4;

  // Null for an IPv4 database.
  readonly AddressRanges<UInt128> ipv6;

  IpCountryDatabase(MmdbCountries countries)
  {
    DatabaseType = countries.DatabaseType;
    BuildTime = countries.BuildTime;
    locations = [.. countries.Countries.Select(code => code == null ? null : new GeoLocation(code))];
    ipv4 = new([.. countries.Ipv4Starts], [.. countries.Ipv4Countries]);
    if (countries.Ipv6Starts != null)
      ipv6 = new([.. countries.Ipv6Starts], [.. countries.Ipv6Countries]);
  }

  /// <summary>The database type from the file's metadata, for example "country ipvAll".</summary>
  public string DatabaseType { get; }

  /// <summary>When the database was built, from the file's metadata.</summary>
  public DateTimeOffset BuildTime { get; }

  /// <summary>
  /// Loads a database file.
  /// </summary>
  /// <exception cref="InvalidDataException">The file is not an MMDB database.</exception>
  public static IpCountryDatabase Load(string path) => new(MmdbCountryReader.Read(File.ReadAllBytes(path)));

  /// <summary>
  /// Loads a database from a stream.
  /// </summary>
  /// <exception cref="InvalidDataException">The stream is not an MMDB database.</exception>
  public static IpCountryDatabase Load(Stream stream)
  {
    ArgumentNullException.ThrowIfNull(stream);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return new(MmdbCountryReader.Read(buffer.ToArray()));
  }

  /// <summary>
  /// Downloads a database, checks that it loads, saves it to a file and returns it. The file is
  /// replaced only by a valid database.
  /// </summary>
  /// <param name="path">The file to save the database to. Its directory is created when missing.</param>
  /// <param name="url">The database to download. Default is <see cref="DefaultUrl"/>.</param>
  /// <param name="httpClient">The client to download with. Default is a new client.</param>
  /// <param name="cancellationToken">Cancels the download.</param>
  /// <exception cref="InvalidDataException">The download is not an MMDB database.</exception>
  public static async Task<IpCountryDatabase> DownloadAsync(string path, string url = DefaultUrl,
      HttpClient httpClient = null, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(url);
    byte[] file;
    using (var ownClient = httpClient == null ? new HttpClient() : null)
      file = await (httpClient ?? ownClient).GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
    var database = new IpCountryDatabase(MmdbCountryReader.Read(file));
    path = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    var download = path + ".download";
    await File.WriteAllBytesAsync(download, file, cancellationToken).ConfigureAwait(false);
    File.Move(download, path, overwrite: true);
    return database;
  }

  /// <summary>
  /// Resolves the country of an IP address. Returns null for an unknown or invalid address.
  /// </summary>
  public GeoLocation Locate(string ipAddress) => ipAddress == null ? null : Locate(ipAddress.AsSpan());

  /// <inheritdoc cref="Locate(string)"/>
  public GeoLocation Locate(ReadOnlySpan<char> ipAddress)
  {
    if (IpAddressText.TryParseIpv4(ipAddress, out var v4))
      return locations[ipv4.Find(v4)];
    if (IpAddressText.TryParseIpv6(ipAddress, out var v6))
      return LocateIpv6(v6);
    return IPAddress.TryParse(ipAddress, out var parsed) ? Locate(parsed) : null;
  }

  /// <inheritdoc cref="Locate(string)"/>
  public GeoLocation Locate(IPAddress ipAddress)
  {
    ArgumentNullException.ThrowIfNull(ipAddress);
    Span<byte> bytes = stackalloc byte[16];
    if (!ipAddress.TryWriteBytes(bytes, out var written)) return null;
    return ipAddress.AddressFamily switch
    {
      AddressFamily.InterNetwork when written == 4 => locations[ipv4.Find(BinaryPrimitives.ReadUInt32BigEndian(bytes))],
      AddressFamily.InterNetworkV6 when written == 16 => LocateIpv6(BinaryPrimitives.ReadUInt128BigEndian(bytes)),
      _ => null
    };
  }

  GeoLocation LocateIpv6(UInt128 address)
  {
    if (address >> 32 == 0xFFFFu)
      return locations[ipv4.Find((uint)address)];
    return ipv6 == null ? null : locations[ipv6.Find(address)];
  }
}
