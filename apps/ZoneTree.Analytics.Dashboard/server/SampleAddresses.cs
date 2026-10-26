using ZoneTree.Analytics;

// Public IPv4 addresses that a geolocator places in given countries, so generated visitors can
// be geolocated like real ones. Random addresses are probed until every country has enough; the
// same database gives the same addresses.
sealed class SampleAddresses
{
  const int PerCountry = 512;

  // Enough for countries with a twentieth of a percent of the address space.
  const int MaxProbes = 4_000_000;

  readonly Dictionary<string, string[]> addresses;

  SampleAddresses(Dictionary<string, string[]> addresses) => this.addresses = addresses;

  public static SampleAddresses Find(IGeoLocator locator, IEnumerable<string> countries)
  {
    var found = countries.ToDictionary(country => country, _ => new List<string>(PerCountry), StringComparer.Ordinal);
    var missing = found.Count;
    var random = new Random(42);
    for (var probe = 0; probe < MaxProbes && missing > 0; probe++)
    {
      var value = (uint)random.NextInt64(1L << 32);
      var address = $"{value >> 24}.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";
      var country = locator.Locate(address)?.Country;
      if (country == null || !found.TryGetValue(country, out var list) || list.Count == PerCountry) continue;
      list.Add(address);
      if (list.Count == PerCountry) missing--;
    }
    return new(found.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal));
  }

  // The visitor's address in the country, or null when none was found there.
  public string Of(int visitor, string country)
  {
    var candidates = addresses.GetValueOrDefault(country);
    if (candidates == null || candidates.Length == 0) return null;
    return candidates[(int)((uint)visitor * 2246822519u >> 7) % candidates.Length];
  }
}
