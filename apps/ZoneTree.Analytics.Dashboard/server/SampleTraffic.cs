using ZoneTree.Analytics;

// Realistic website traffic for benchmarks and demo data: page views, goals, purchases and bots,
// generated as a stream, so memory does not grow with the event count.
static class SampleTraffic
{
  // Visitor shares per mille, roughly those of an international developer site.
  static readonly (string Country, int Weight)[] Countries =
  [
    ("US", 280), ("IN", 90), ("DE", 70), ("GB", 60), ("FR", 40), ("CA", 40), ("CN", 35), ("BR", 35),
    ("JP", 30), ("NL", 25), ("RU", 20), ("KR", 20), ("ES", 20), ("IT", 20), ("PL", 20), ("AU", 20),
    ("TR", 15), ("SE", 12), ("UA", 12), ("VN", 12), ("ID", 12), ("CH", 10), ("MX", 10), ("TW", 10),
    ("SG", 10), ("IL", 8), ("BE", 8), ("AT", 8), ("CZ", 7), ("AR", 7), ("PK", 6), ("NG", 6),
    ("BD", 5), ("PH", 5), ("EG", 4), ("ZA", 4), ("NO", 4), ("DK", 4), ("FI", 4), ("PT", 4),
    ("IE", 4), ("RO", 4), ("GR", 3), ("HU", 3), ("NZ", 3), ("CO", 3), ("CL", 3), ("TH", 3),
    ("MY", 3), ("KE", 2),
  ];

  static readonly int CountryWeights = Countries.Sum(country => country.Weight);

  public static IEnumerable<string> CountryCodes => Countries.Select(country => country.Country);

  // Addresses in the sample countries, for traffic that a geolocator resolves.
  public static SampleAddresses FindAddresses(IGeoLocator locator) => SampleAddresses.Find(locator, CountryCodes);

  // A visitor keeps its country. The hash spreads visitors independently of the device and
  // address choices, which use the visitor number modulo small values.
  static string CountryOf(int visitor)
  {
    var point = (int)((ulong)((uint)visitor * 2654435761u) * (ulong)CountryWeights >> 32);
    foreach (var (country, weight) in Countries)
    {
      if (point < weight) return country;
      point -= weight;
    }
    return Countries[^1].Country;
  }

  // Demo traffic for an empty site: 10,000 events over the last 60 days, through the real engine.
  public static async Task SeedAsync(AnalyticsEngine engine)
  {
    foreach (var item in CreateEvents(10_000, 60, timeZone: engine.TimeZone))
      if (!await engine.TrackAsync(item))
        throw new InvalidOperationException("The engine did not accept a demo event.");
    await engine.FlushAsync();
  }

  // With addresses, a visitor's country comes from its address, which the engine geolocates;
  // otherwise it is set as a dimension.
  public static IEnumerable<AnalyticsEvent> CreateEvents(
      int eventCount, int days, string site = null, TimeZoneInfo timeZone = null,
      DateTimeOffset? endingAt = null, SampleAddresses addresses = null)
  {
    if (eventCount is < 1 or > 10_000_000)
      throw new ArgumentOutOfRangeException(nameof(eventCount), "Choose between 1 and 10,000,000 events.");
    if (days is < 1 or > 365)
      throw new ArgumentOutOfRangeException(nameof(days), "Choose between 1 and 365 days.");
    var random = new Random(42);
    var batch = Guid.NewGuid().ToString("N");
    var end = endingAt ?? DateTimeOffset.UtcNow.AddSeconds(-1);
    var start = end.AddDays(-days);
    var profile = new SampleTrafficProfile(start, end, timeZone ?? TimeZoneInfo.Utc);
    string[] paths = ["/", "/docs", "/pricing", "/blog", "/integrations", "/docs/getting-started"];
    string[] referrers = ["", "https://www.google.com/", "https://github.com/", "https://www.bing.com/"];
    const string desktop = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36";
    const string mobile = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1";
    var index = 0;
    var session = 0;
    while (index < eventCount)
    {
      var moment = profile.At((index + random.NextDouble()) / eventCount);
      var campaign = moment.Campaign / (1 + moment.Campaign);
      var bot = random.NextDouble() < 0.025;
      var returning = session > 1024 && moment.Timestamp - start > TimeSpan.FromHours(1)
          && random.NextDouble() < 0.25;
      var visitor = returning ? random.Next(session / 2) : session;
      var onMobile = visitor % 10 < 4;
      var bounce = !bot && random.NextDouble() < 0.4 + (onMobile ? 0.06 : 0) - campaign * 0.18;
      var pageCount = bounce ? 1 : random.Next(2, 7);
      var signup = !bot && !bounce && random.NextDouble() < 0.08 + campaign * 0.16;
      var purchase = signup && random.NextDouble() < 0.2 + campaign * 0.12;
      var length = Math.Min(eventCount - index, pageCount + (signup ? 1 : 0) + (purchase ? 1 : 0));
      var source = random.NextDouble();
      var referrer = campaign > 0.3 && source < 0.55 ? referrers[2]
          : source < 0.45 ? referrers[1] : source < 0.8 ? referrers[0]
          : source < 0.92 ? referrers[2] : referrers[3];
      var timestamp = moment.Timestamp;
      for (var offset = 0; offset < length; offset++)
      {
        var name = offset < pageCount ? "pageview" : offset == pageCount ? "signup" : "purchase";
        var path = offset >= pageCount ? "/pricing" : bot ? paths[random.Next(paths.Length)]
            : offset == 0 ? (campaign > 0.3 ? "/blog" : source < 0.45 ? "/docs" : "/")
            : offset == 1 ? "/pricing" : paths[random.Next(paths.Length)];
        var item = new AnalyticsEvent(name)
        {
          Site = site,
          Timestamp = timestamp,
          VisitorId = bot ? null : $"sample-{batch}-{visitor}",
          Url = "https://sample.example" + path,
          Referrer = referrer,
          IpAddress = visitor % 5 < 3 ? "203.0.113.1" : "203.0.113.2",
          UserAgent = bot ? "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"
              : onMobile ? mobile : desktop,
        };
        if (!bot)
        {
          var country = CountryOf(visitor);
          var address = addresses?.Of(visitor, country);
          if (address != null) item.IpAddress = address;
          // Set like a CDN country header would be, so no engine needs a fake geolocator.
          else item.Dimensions[StandardDimensions.Country] = country;
        }
        if (name == "purchase")
        {
          var team = random.NextDouble() < 0.22;
          item.Dimensions["plan"] = team ? "team" : "pro";
          item.Values["revenue"] = team ? 149 : 49;
        }
        if (name == "pageview")
          item.Values["load_time"] = (int)(120 + Math.Pow(random.NextDouble(), 3) * 1100 + campaign * 250);
        yield return item;
        // Real visit lengths, instead of stretching one visit over hours or days.
        timestamp = timestamp.AddSeconds(random.Next(5, 46));
        if (timestamp > end) timestamp = end;
      }
      index += length;
      session++;
    }
  }
}
