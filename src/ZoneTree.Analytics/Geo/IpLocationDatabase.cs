using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ZoneTree.Analytics.Geo;

namespace ZoneTree.Analytics;

/// <summary>
/// Resolves IP addresses to locations from MaxMind DB (MMDB) files, for example the
/// <see href="https://github.com/sapics/ip-location-db">ip-location-db</see> country and city
/// databases, DB-IP Lite or GeoLite2. Countries are ISO 3166-1 alpha-2 codes; regions and cities
/// are English names.
/// </summary>
/// <remarks>
/// <para>
/// Loading flattens the files into sorted address ranges, so a lookup reads a prefix index and
/// binary searches a few ranges instead of walking the search tree bit by bit. Lookups do not
/// allocate and are thread-safe. IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) resolve as IPv4.
/// </para>
/// <para>
/// Locations are kept to the loaded precision: a city database loaded for countries merges its
/// ranges by country and takes far less memory. The user-country database, about 650,000 IPv4
/// and IPv6 ranges, takes about 9 MB.
/// </para>
/// <para>
/// A database does not change. <see cref="IpLocationProvider"/> downloads databases, keeps them
/// current and swaps in new ones.
/// </para>
/// </remarks>
public sealed class IpLocationDatabase : IGeoLocator
{
  // Index 0 is unknown.
  readonly GeoLocation[] locations;

  // Null when no file has IPv4 locations.
  readonly AddressRanges<uint> ipv4;

  // Null when no file has IPv6 locations.
  readonly AddressRanges<UInt128> ipv6;

  IpLocationDatabase(GeoPrecision precision, LocationTable table, List<MmdbFile> files)
  {
    Precision = precision;
    AvailablePrecision = table.Available;
    DatabaseType = string.Join(", ", files.Select(file => file.DatabaseType).Where(type => !string.IsNullOrEmpty(type)).Distinct());
    BuildTime = files.Min(file => file.BuildTime);
    locations = [.. table.Locations];
    // Each address family comes from the first file with locations in it, so IPv4 and IPv6
    // can come in one file or in separate ones.
    var ipv4Ranges = files.Select(file => file.Ipv4).FirstOrDefault(ranges => ranges.HasLocations);
    if (ipv4Ranges != null)
      ipv4 = new([.. ipv4Ranges.Starts], [.. ipv4Ranges.Locations]);
    var ipv6Ranges = files.Select(file => file.Ipv6).FirstOrDefault(ranges => ranges?.HasLocations == true);
    if (ipv6Ranges != null)
      ipv6 = new([.. ipv6Ranges.Starts], [.. ipv6Ranges.Locations]);
  }

  /// <summary>The most precise location the database resolves.</summary>
  public GeoPrecision Precision { get; }

  /// <summary>
  /// The most precise location in the files, which can exceed <see cref="Precision"/>: loading
  /// them with a higher precision keeps more. None when the files have no locations.
  /// </summary>
  public GeoPrecision AvailablePrecision { get; }

  /// <summary>The database types from the files' metadata, for example "country ipvAll".</summary>
  public string DatabaseType { get; }

  /// <summary>When the oldest file was built, from the files' metadata.</summary>
  public DateTimeOffset BuildTime { get; }

  /// <summary>The number of distinct locations.</summary>
  public int LocationCount => locations.Length - 1;

  /// <summary>The number of IPv4 and IPv6 address ranges.</summary>
  public int RangeCount => (ipv4?.Count ?? 0) + (ipv6?.Count ?? 0);

  /// <summary>
  /// Loads a database file.
  /// </summary>
  /// <param name="path">The MMDB file.</param>
  /// <param name="precision">The most precise location to keep. Default is the country.</param>
  /// <exception cref="InvalidDataException">The file is not an MMDB database.</exception>
  public static IpLocationDatabase Load(string path, GeoPrecision precision = GeoPrecision.Country) => Load([path], precision);

  /// <summary>
  /// Loads a database from several files, for example separate IPv4 and IPv6 files. Each address
  /// family comes from the first file with locations in it.
  /// </summary>
  /// <param name="paths">The MMDB files.</param>
  /// <param name="precision">The most precise location to keep. Default is the country.</param>
  /// <exception cref="InvalidDataException">A file is not an MMDB database.</exception>
  public static IpLocationDatabase Load(IEnumerable<string> paths, GeoPrecision precision = GeoPrecision.Country)
  {
    ArgumentNullException.ThrowIfNull(paths);
    return Load(paths.Select(IpLocationFiles.Read), precision);
  }

  /// <summary>
  /// Loads a database from a stream.
  /// </summary>
  /// <param name="stream">The MMDB data.</param>
  /// <param name="precision">The most precise location to keep. Default is the country.</param>
  /// <exception cref="InvalidDataException">The stream is not an MMDB database.</exception>
  public static IpLocationDatabase Load(Stream stream, GeoPrecision precision = GeoPrecision.Country)
  {
    ArgumentNullException.ThrowIfNull(stream);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return Load([buffer.ToArray()], precision);
  }

  // Each file is read, flattened and released before the next.
  internal static IpLocationDatabase Load(IEnumerable<byte[]> files, GeoPrecision precision)
  {
    if (precision == GeoPrecision.None)
      throw new ArgumentOutOfRangeException(nameof(precision), "Choose the country, region or city precision.");
    var table = new LocationTable();
    var read = files.Select(file => MmdbReader.Read(file, precision, table)).ToList();
    if (read.Count == 0)
      throw new ArgumentException("At least one database file is required.");
    return new(precision, table, read);
  }

  /// <summary>
  /// Resolves the location of an IP address. Returns null for an unknown or invalid address.
  /// </summary>
  public GeoLocation Locate(string ipAddress) => ipAddress == null ? null : Locate(ipAddress.AsSpan());

  /// <inheritdoc cref="Locate(string)"/>
  public GeoLocation Locate(ReadOnlySpan<char> ipAddress)
  {
    if (IpAddressText.TryParseIpv4(ipAddress, out var v4))
      return LocateIpv4(v4);
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
      AddressFamily.InterNetwork when written == 4 => LocateIpv4(BinaryPrimitives.ReadUInt32BigEndian(bytes)),
      AddressFamily.InterNetworkV6 when written == 16 => LocateIpv6(BinaryPrimitives.ReadUInt128BigEndian(bytes)),
      _ => null
    };
  }

  GeoLocation LocateIpv4(uint address) => ipv4 == null ? null : locations[ipv4.Find(address)];

  GeoLocation LocateIpv6(UInt128 address)
  {
    if (address >> 32 == 0xFFFFu)
      return LocateIpv4((uint)address);
    return ipv6 == null ? null : locations[ipv6.Find(address)];
  }
}
