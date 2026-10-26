using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using ZoneTree.Analytics.Geo;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class IpCountryDatabaseTests : DirectoryTest
{
  // ip-location-db records ({country_code}) and MaxMind records ({country: {iso_code}}).
  static byte[] Database(int ipVersion = 6, bool aliasSixToFour = false)
  {
    var writer = new MmdbWriter(ipVersion);
    writer.Insert("81.2.69.0/24", writer.CountryCode("GB"));
    writer.Insert("1.0.0.0/8", writer.CountryCode("AU"));
    writer.Insert("255.255.255.255/32", writer.CountryCode("ZZ"));
    if (ipVersion == 6)
    {
      writer.Insert("2a01:4f8::/32", writer.CountryCode("DE"));
      writer.Insert("2001:db8:1::/48", writer.MaxMindCountry("US", registered: "CA"));
      writer.Insert("2001:db8:2::/48", writer.MaxMindCountry(null, registered: "FR"));
      writer.Insert("2001:db8:3::7/128", writer.CountryCode("NL"));
      if (aliasSixToFour) writer.AliasToIpv4("2002::/16");
    }
    return writer.ToArray();
  }

  static IpCountryDatabase Load(byte[] file) => IpCountryDatabase.Load(new MemoryStream(file));

  [TestCase("81.2.69.0", "GB")]
  [TestCase("81.2.69.160", "GB")]
  [TestCase("81.2.69.255", "GB")]
  [TestCase("81.2.68.255", null)]
  [TestCase("81.2.70.0", null)]
  [TestCase("1.0.0.0", "AU")]
  [TestCase("1.255.255.255", "AU")]
  [TestCase("0.0.0.0", null)]
  [TestCase("255.255.255.254", null)]
  [TestCase("255.255.255.255", "ZZ")]
  [TestCase("::ffff:81.2.69.160", "GB")]
  [TestCase("::FFFF:5102:45A0", "GB")]
  [TestCase("2a01:4f8::", "DE")]
  [TestCase("2a01:4f8:c17:1a3b::1", "DE")]
  [TestCase("2A01:04F8:FFFF:FFFF:FFFF:FFFF:FFFF:FFFF", "DE")]
  [TestCase("2a01:4f7:ffff:ffff:ffff:ffff:ffff:ffff", null)]
  [TestCase("2a01:4f9::", null)]
  [TestCase("2001:db8:1:2::5", "US")]
  [TestCase("2001:db8:2::1", "FR")]
  [TestCase("2001:db8:3::6", null)]
  [TestCase("2001:db8:3::7", "NL")]
  [TestCase("2001:db8:3::8", null)]
  [TestCase("::", null)]
  [TestCase("::1", null)]
  [TestCase("fe80::1%3", null)]
  [TestCase("2a01:4f8::1%2", "DE")]
  [TestCase("1.2.3.04", "AU")]
  [TestCase("", null)]
  [TestCase("unknown", null)]
  [TestCase("1.2.3.4.5", null)]
  [TestCase(null, null)]
  public void LocatesTheCountryOfAnAddress(string address, string country)
  {
    var database = Load(Database());
    Assert.Multiple(() =>
    {
      Assert.That(database.Locate(address)?.Country, Is.EqualTo(country));
      if (IPAddress.TryParse(address, out var parsed))
        Assert.That(database.Locate(parsed)?.Country, Is.EqualTo(country));
    });
  }

  [Test]
  public void ExposesMetadataAndSharesLocations()
  {
    var database = Load(Database());
    Assert.Multiple(() =>
    {
      Assert.That(database.DatabaseType, Is.EqualTo("test-country"));
      Assert.That(database.BuildTime, Is.EqualTo(MmdbWriter.BuildTime));
      Assert.That(database.Locate("1.2.3.4"), Is.SameAs(database.Locate("1.9.9.9")));
      Assert.That(database.Locate("1.2.3.4"), Is.EqualTo(new GeoLocation("AU")));
    });
  }

  [Test]
  public void Ipv4DatabaseResolvesIpv4AndMappedAddresses()
  {
    var database = Load(Database(ipVersion: 4));
    Assert.Multiple(() =>
    {
      Assert.That(database.Locate("81.2.69.160")?.Country, Is.EqualTo("GB"));
      Assert.That(database.Locate("::ffff:1.2.3.4")?.Country, Is.EqualTo("AU"));
      Assert.That(database.Locate("2a01:4f8::1"), Is.Null);
    });
  }

  [Test]
  public void AliasesToTheIpv4SubtreeAreUnknown()
  {
    var database = Load(Database(aliasSixToFour: true));
    Assert.Multiple(() =>
    {
      // 2002:5102:45a0:: is 6to4 for 81.2.69.160.
      Assert.That(database.Locate("2002:5102:45a0::"), Is.Null);
      Assert.That(database.Locate("81.2.69.160")?.Country, Is.EqualTo("GB"));
      Assert.That(database.Locate("2a01:4f8::1")?.Country, Is.EqualTo("DE"));
    });
  }

  [Test]
  public void ManyRangesAreSearchedThroughCrowdedSlices()
  {
    // Thousands of alternating /24 and /64 networks next to each other, with a distant network
    // that widens the index slices, crowd single slices: lookups go through the sampled starts.
    var writer = new MmdbWriter(6);
    string[] codes = ["AA", "BB", "CC"];
    var records = codes.Select(writer.CountryCode).ToArray();
    writer.Insert("223.0.0.0/8", records[0]);
    writer.Insert("2c0f::/16", records[0]);
    for (var i = 0; i < 3000; i++)
    {
      writer.Insert($"10.{i >> 8}.{i & 255}.0/24", records[i % 3]);
      writer.Insert($"2a00:1:2:{i:x}::/64", records[i % 3]);
    }
    var database = Load(writer.ToArray());
    var random = new Random(7);
    for (var n = 0; n < 2000; n++)
    {
      var i = random.Next(3000);
      var expected = codes[i % 3];
      Assert.That(database.Locate($"10.{i >> 8}.{i & 255}.{random.Next(256)}")?.Country, Is.EqualTo(expected));
      Assert.That(database.Locate($"2a00:1:2:{i:x}:{random.Next(65536):x}::{random.Next(65536):x}")?.Country, Is.EqualTo(expected));
    }
    Assert.Multiple(() =>
    {
      Assert.That(database.Locate("10.11.184.0"), Is.Null);
      Assert.That(database.Locate("2a00:1:2:bb8::"), Is.Null);
      Assert.That(database.Locate("2a00:1:1:ffff::"), Is.Null);
    });
  }

  [Test]
  public void FastParsersAgreeWithIPAddress()
  {
    var random = new Random(11);
    var accepted = new List<string>();
    for (var i = 0; i < 20_000; i++)
    {
      var bytes = new byte[i % 2 == 0 ? 4 : 16];
      random.NextBytes(bytes);
      // Runs of zero groups produce "::" in different places.
      if (bytes.Length == 16)
        for (var k = random.Next(16); k < 16 && random.Next(3) > 0; k++) bytes[k] = 0;
      var address = new IPAddress(bytes);
      var text = address.ToString();
      accepted.Add(text);
      accepted.Add(text.ToUpperInvariant());
      if (bytes.Length == 16)
        accepted.Add(string.Join(':', Enumerable.Range(0, 8).Select(g => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(g * 2)).ToString("x4", CultureInfo.InvariantCulture))));
    }
    accepted.AddRange(["::", "::1", "1::", "1:2:3:4:5:6:7::", "::2:3:4:5:6:7:8", "::ffff:1.2.3.4", "::1.2.3.4", "1:2:3:4:5:6:1.2.3.4", "0.0.0.0"]);
    foreach (var text in accepted)
      Assert.That(Parse(text), Is.EqualTo(IPAddress.Parse(text)), text);

    string[] rejected = [":::", "1::2::3", "1:2:3:4:5:6:7:8:9", "1:2:3:4:5:6:7", "12345::", ":1::", "1:", "1::2:", ":",
        "1:2:3:4:5:6:7:8::", "1:2:3:4:5:6:7:1.2.3.4", "::1.2.3", "::1.2.3.4:5", "::ffff:1.2.3.04", "g::",
        "1.2.3", "1.2.3.4.5", "256.1.1.1", "01.2.3.4", "1..2.3", ".1.2.3", "1.2.3.", "1.2.3.4 ", "1234.1.1.1", ""];
    foreach (var text in rejected)
      Assert.That(Parse(text), Is.Null, text);

    static IPAddress Parse(string text)
    {
      if (IpAddressText.TryParseIpv4(text, out var ipv4)) return new(BinaryPrimitives.ReverseEndianness(ipv4));
      if (!IpAddressText.TryParseIpv6(text, out var ipv6)) return null;
      var bytes = new byte[16];
      BinaryPrimitives.WriteUInt128BigEndian(bytes, ipv6);
      return new(bytes);
    }
  }

  [Test]
  public void RejectsInvalidFiles()
  {
    var valid = Database();
    var random = new byte[4096];
    new Random(3).NextBytes(random);
    var wrongRecordSize = new MmdbWriter(6) { RecordSize = 20 }.ToArray();
    var wrongVersion = new MmdbWriter(6) { MajorVersion = 3 }.ToArray();
    Assert.Multiple(() =>
    {
      Assert.Throws<InvalidDataException>(() => Load(random));
      Assert.Throws<InvalidDataException>(() => Load([]));
      // The metadata without the search tree it describes.
      Assert.Throws<InvalidDataException>(() => Load(valid[^250..]));
      Assert.Throws<InvalidDataException>(() => Load(wrongRecordSize));
      Assert.Throws<InvalidDataException>(() => Load(wrongVersion));
    });
  }

  [Test]
  public async Task DownloadSavesOnlyValidDatabases()
  {
    var path = Path.Combine(directory, "geo", "countries.mmdb");
    var valid = Database();
    using (var client = new HttpClient(new StaticHandler(valid)))
    {
      var database = await IpCountryDatabase.DownloadAsync(path, "https://example.test/countries.mmdb", client);
      Assert.That(database.Locate("81.2.69.160")?.Country, Is.EqualTo("GB"));
    }
    Assert.That(File.ReadAllBytes(path), Is.EqualTo(valid));

    using (var client = new HttpClient(new StaticHandler(Encoding.UTF8.GetBytes("<html>Not found</html>"))))
      Assert.ThrowsAsync<InvalidDataException>(() => IpCountryDatabase.DownloadAsync(path, "https://example.test/countries.mmdb", client));
    Assert.Multiple(() =>
    {
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(valid));
      Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)), Has.Length.EqualTo(1));
      Assert.That(IpCountryDatabase.Load(path).Locate("2a01:4f8::1")?.Country, Is.EqualTo("DE"));
    });
  }

  [Test]
  public async Task EngineResolvesVisitorCountries()
  {
    var database = Load(Database());
    using var engine = TestEngine.Create(directory, options => options.Web.GeoLocator = database);
    var at = new DateTime(2026, 3, 2, 12, 0, 0);
    string[] addresses = ["81.2.69.160", "::ffff:81.2.69.161", "2a01:4f8::1", "192.0.2.1"];
    foreach (var address in addresses)
      engine.Track(new AnalyticsEvent(StandardEvents.PageView)
      {
        Url = "https://mysite.com/", IpAddress = address, UserAgent = UserAgents.ChromeWindows, Timestamp = Utc(at)
      });
    engine.Flush();
    var countries = await engine.Web.GetBreakdownAsync(new WebReportRequest { Range = Day(at.Date) }, StandardDimensions.Country);
    // 192.0.2.1 has no country.
    Assert.That(countries.Rows.Where(row => !string.IsNullOrEmpty(row.Value)).ToDictionary(row => row.Value, row => row.Visitors),
        Is.EquivalentTo(new Dictionary<string, long> { ["GB"] = 2, ["DE"] = 1 }));
  }

  sealed class StaticHandler(byte[] content) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
  }
}

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
