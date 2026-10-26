using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Writes small MaxMind DB files with 24-bit records. In an IPv6 file, IPv4 networks are under ::/96.
/// </summary>
sealed class MmdbWriter(int ipVersion)
{
  public static readonly DateTimeOffset BuildTime = new(2026, 10, 25, 23, 38, 2, TimeSpan.Zero);

  const long Empty = -1;

  // A record is a node index, Empty, or data at offset -(record + 2).
  readonly List<long[]> nodes = [[Empty, Empty]];
  readonly List<byte> data = [];
  readonly Dictionary<string, int> strings = [];

  public int RecordSize { get; init; } = 24;

  public int MajorVersion { get; init; } = 2;

  public long CountryCode(string code)
  {
    var record = Record();
    Map(1);
    // Keys repeat through pointers, as real files store them.
    Key("country_code");
    String(code);
    return record;
  }

  public long MaxMindCountry(string country, string registered)
  {
    var record = Record();
    Map(country == null ? 2 : 3);
    Key("continent");
    Map(1);
    Key("code");
    String("EU");
    if (country != null)
    {
      Key("country");
      Map(3);
      Key("geoname_id");
      data.AddRange([0xC4, 0, 0x2C, 0x2B, 0x63]);
      Key("iso_code");
      String(country);
      Key("names");
      Map(1);
      Key("en");
      String("Country");
    }
    Key("registered_country");
    Map(1);
    Key("iso_code");
    String(registered);
    return record;
  }

  // An ip-location-db city record. Empty strings mean unknown.
  public long SapicsCity(string country, string state, string city)
  {
    var record = Record();
    Map(6);
    Key("city"); String(city);
    Key("country_code"); String(country);
    Key("latitude"); Double(52.52);
    Key("postcode"); String("");
    Key("state1"); String(state);
    Key("state2"); String("");
    return record;
  }

  // A MaxMind city record: English names, subdivisions from the largest.
  public long MaxMindCity(string country, string subdivision, string city)
  {
    var record = Record();
    Map(4);
    Key("city"); Map(2); Key("geoname_id"); data.AddRange([0xC4, 0, 0, 0, 1]); Key("names"); Map(2); Key("de"); String(city + " (de)"); Key("en"); String(city);
    Key("country"); Map(1); Key("iso_code"); String(country);
    Key("location"); Map(2); Key("latitude"); Double(48.1); Key("longitude"); Double(11.6);
    Key("subdivisions"); data.AddRange([0x02, 0x04]);
    Map(2); Key("iso_code"); String("BY"); Key("names"); Map(1); Key("en"); String(subdivision);
    Map(1); Key("names"); Map(1); Key("en"); String("Lower subdivision");
    return record;
  }

  void Double(double value)
  {
    data.Add(0x68);
    data.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(BitConverter.DoubleToInt64Bits(value))));
  }

  public void Insert(string network, long record)
  {
    var (prefix, length) = Network(network);
    var node = 0;
    for (var depth = 0; depth < length - 1; depth++)
    {
      var bit = Bit(prefix, depth);
      if (nodes[node][bit] == Empty)
      {
        nodes[node][bit] = nodes.Count;
        nodes.Add([Empty, Empty]);
      }
      node = (int)nodes[node][bit];
    }
    nodes[node][Bit(prefix, length - 1)] = record;
  }

  // Points an IPv6 network at the IPv4 subtree, as MaxMind files do for 6to4 and Teredo.
  public void AliasToIpv4(string network)
  {
    var ipv4 = 0;
    for (var depth = 0; depth < 96; depth++)
      ipv4 = (int)nodes[ipv4][0];
    Insert(network, ipv4);
  }

  public byte[] ToArray()
  {
    var file = new List<byte>();
    long nodeCount = nodes.Count;
    foreach (var node in nodes)
      foreach (var record in node)
      {
        var value = record == Empty ? nodeCount : record >= 0 ? record : nodeCount + 16 + -(record + 2);
        file.AddRange([(byte)(value >> 16), (byte)(value >> 8), (byte)value]);
      }
    file.AddRange(new byte[16]);
    file.AddRange(data);
    file.AddRange([0xAB, 0xCD, 0xEF, .. "MaxMind.com"u8.ToArray()]);
    data.Clear();
    strings.Clear();
    Map(9);
    Key("binary_format_major_version"); UInt16(MajorVersion);
    Key("binary_format_minor_version"); UInt16(0);
    Key("build_epoch"); data.AddRange([0x08, 0x02, .. BigEndian((ulong)BuildTime.ToUnixTimeSeconds())]);
    Key("database_type"); String("test-country");
    Key("description"); Map(1); Key("en"); String("Test");
    Key("ip_version"); UInt16(ipVersion);
    Key("languages"); data.AddRange([0x01, 0x04]); String("en");
    Key("node_count"); data.AddRange([0xC4, .. BigEndian((ulong)nodeCount)[4..]]);
    Key("record_size"); UInt16(RecordSize);
    file.AddRange(data);
    return [.. file];
  }

  long Record() => -(data.Count + 2);

  (UInt128 Prefix, int Length) Network(string network)
  {
    var parts = network.Split('/');
    var bytes = IPAddress.Parse(parts[0]).GetAddressBytes();
    var length = int.Parse(parts[1], CultureInfo.InvariantCulture);
    if (bytes.Length == 4)
      return ipVersion == 6
          ? (BinaryPrimitives.ReadUInt32BigEndian(bytes), 96 + length)
          : ((UInt128)BinaryPrimitives.ReadUInt32BigEndian(bytes) << 96, length);
    return (BinaryPrimitives.ReadUInt128BigEndian(bytes), length);
  }

  static int Bit(UInt128 prefix, int depth) => (int)(prefix >> (127 - depth)) & 1;

  static byte[] BigEndian(ulong value)
  {
    var bytes = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
    return bytes;
  }

  void Map(int size) => data.Add((byte)(0xE0 | size));

  void UInt16(int value) => data.AddRange([0xA2, (byte)(value >> 8), (byte)value]);

  void String(string value)
  {
    var bytes = Encoding.UTF8.GetBytes(value);
    data.Add((byte)(0x40 | bytes.Length));
    data.AddRange(bytes);
  }

  void Key(string key)
  {
    if (strings.TryGetValue(key, out var offset))
    {
      data.AddRange([(byte)(0x20 | offset >> 8), (byte)offset]);
      return;
    }
    strings[key] = data.Count;
    String(key);
  }
}
