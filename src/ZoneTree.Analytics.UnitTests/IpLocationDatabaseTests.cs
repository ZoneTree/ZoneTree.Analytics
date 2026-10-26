using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using ZoneTree.Analytics.Geo;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class IpLocationDatabaseTests : DirectoryTest
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

  static IpLocationDatabase Load(byte[] file) => IpLocationDatabase.Load(new MemoryStream(file));

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

  // Cities in both record layouts: Berlin and Hamburg share a region, Munich does not.
  static byte[] Cities(int ipVersion = 6, bool ipv4 = true, bool ipv6 = true)
  {
    var writer = new MmdbWriter(ipVersion);
    if (ipv4)
    {
      writer.Insert("5.1.0.0/16", writer.SapicsCity("DE", "Berlin", "Berlin"));
      writer.Insert("5.2.0.0/16", writer.SapicsCity("DE", "Berlin", "Spandau"));
      writer.Insert("5.3.0.0/16", writer.SapicsCity("DE", "Hamburg", "Hamburg"));
      writer.Insert("5.4.0.0/16", writer.MaxMindCity("DE", "Bavaria", "Munich"));
      writer.Insert("5.5.0.0/16", writer.SapicsCity("FR", "", ""));
    }
    if (ipv6)
    {
      writer.Insert("2a02:1::/32", writer.SapicsCity("DE", "Berlin", "Berlin"));
      writer.Insert("2a02:2::/32", writer.MaxMindCity("DE", "Bavaria", "Munich"));
    }
    return writer.ToArray();
  }

  [TestCase(GeoPrecision.City, "5.2.0.1", "DE", "Berlin", "Spandau")]
  [TestCase(GeoPrecision.City, "5.4.0.1", "DE", "Bavaria", "Munich")]
  [TestCase(GeoPrecision.City, "2a02:2::1", "DE", "Bavaria", "Munich")]
  [TestCase(GeoPrecision.City, "5.5.0.1", "FR", null, null)]
  [TestCase(GeoPrecision.Region, "5.2.0.1", "DE", "Berlin", null)]
  [TestCase(GeoPrecision.Region, "5.3.0.1", "DE", "Hamburg", null)]
  [TestCase(GeoPrecision.Country, "5.4.0.1", "DE", null, null)]
  [TestCase(GeoPrecision.Country, "2a02:1::1", "DE", null, null)]
  public void ResolvesRegionsAndCitiesToThePrecision(GeoPrecision precision, string address, string country, string region, string city)
  {
    var database = IpLocationDatabase.Load(new MemoryStream(Cities()), precision);
    Assert.Multiple(() =>
    {
      Assert.That(database.Precision, Is.EqualTo(precision));
      Assert.That(database.Locate(address), Is.EqualTo(new GeoLocation(country, region, city)));
    });
  }

  [Test]
  public void LowerPrecisionMergesRanges()
  {
    var city = IpLocationDatabase.Load(new MemoryStream(Cities()), GeoPrecision.City);
    var region = IpLocationDatabase.Load(new MemoryStream(Cities()), GeoPrecision.Region);
    var country = IpLocationDatabase.Load(new MemoryStream(Cities()), GeoPrecision.Country);
    Assert.Multiple(() =>
    {
      // Berlin, Spandau, Hamburg, Munich and France; Berlin twice from both families.
      Assert.That(city.LocationCount, Is.EqualTo(5));
      Assert.That(region.LocationCount, Is.EqualTo(4));
      Assert.That(country.LocationCount, Is.EqualTo(2));
      Assert.That(region.RangeCount, Is.LessThan(city.RangeCount));
      Assert.That(country.RangeCount, Is.LessThan(region.RangeCount));
      // The four German /16s are one range at country precision.
      Assert.That(country.Locate("5.1.0.0"), Is.SameAs(country.Locate("5.4.255.255")));
      // The files have cities whatever is kept.
      Assert.That(country.AvailablePrecision, Is.EqualTo(GeoPrecision.City));
      Assert.That(Load(Database()).AvailablePrecision, Is.EqualTo(GeoPrecision.Country));
      Assert.Throws<ArgumentOutOfRangeException>(() => IpLocationDatabase.Load(new MemoryStream(Cities()), GeoPrecision.None));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void CombinesSeparateIpv4AndIpv6Files(bool ipv6First)
  {
    var ipv4 = Path.Combine(directory, "city-ipv4.mmdb");
    var ipv6 = Path.Combine(directory, "city-ipv6.mmdb");
    Directory.CreateDirectory(directory);
    File.WriteAllBytes(ipv4, Cities(ipVersion: 4, ipv6: false));
    File.WriteAllBytes(ipv6, Cities(ipv4: false));
    var database = IpLocationDatabase.Load(ipv6First ? [ipv6, ipv4] : [ipv4, ipv6], GeoPrecision.City);
    Assert.Multiple(() =>
    {
      Assert.That(database.Locate("5.4.0.1")?.City, Is.EqualTo("Munich"));
      Assert.That(database.Locate("::ffff:5.1.0.1")?.City, Is.EqualTo("Berlin"));
      Assert.That(database.Locate("2a02:1::1")?.City, Is.EqualTo("Berlin"));
      // One location for Berlin from both files.
      Assert.That(database.Locate("5.1.0.1"), Is.SameAs(database.Locate("2a02:1::1")));
      Assert.That(database.DatabaseType, Is.EqualTo("test-country"));
    });
    var ipv4Only = IpLocationDatabase.Load(ipv4, GeoPrecision.City);
    Assert.That(ipv4Only.Locate("2a02:1::1"), Is.Null);
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
}
