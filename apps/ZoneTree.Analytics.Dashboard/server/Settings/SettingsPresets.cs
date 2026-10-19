using ZoneTree.Analytics;

// Starting points offered by the setup wizard and the admin panel. Each preset sets the listed
// settings and leaves the rest unchanged.
static class SettingsPresets
{
  public static readonly IReadOnlyList<SettingsPreset> All =
  [
    new("collection", "privacy-first", "Privacy first",
        "No cookies, country-level location, no versions or query strings. Raw events for 30 days.",
        new Dictionary<string, object>
        {
          ["privacy.visitorIdentification"] = nameof(VisitorIdentification.DailyRotatingHash),
          ["privacy.geoPrecision"] = nameof(GeoPrecision.Country),
          ["privacy.collectUserAgentDetails"] = true,
          ["privacy.collectVersions"] = false,
          ["privacy.includeQueryString"] = false,
          ["retention.rawEventsDays"] = 30,
          ["retention.minuteRollupsDays"] = 2,
          ["retention.hourRollupsDays"] = 90,
          ["retention.dayRollupsDays"] = 730,
        }),
    new("collection", "standard", "Standard",
        "No cookies, city-level location. Raw events for a year, daily reports forever.",
        new Dictionary<string, object>
        {
          ["privacy.visitorIdentification"] = nameof(VisitorIdentification.DailyRotatingHash),
          ["privacy.geoPrecision"] = nameof(GeoPrecision.City),
          ["privacy.collectUserAgentDetails"] = true,
          ["privacy.collectVersions"] = false,
          ["privacy.includeQueryString"] = false,
          ["retention.rawEventsDays"] = 365,
          ["retention.minuteRollupsDays"] = 2,
          ["retention.hourRollupsDays"] = 400,
          ["retention.dayRollupsDays"] = null,
        }),
    new("collection", "full-detail", "Full detail",
        "Recognizes returning visitors, keeps versions and query strings. Raw events for three years.",
        new Dictionary<string, object>
        {
          ["privacy.visitorIdentification"] = nameof(VisitorIdentification.PersistentHash),
          ["privacy.geoPrecision"] = nameof(GeoPrecision.City),
          ["privacy.collectUserAgentDetails"] = true,
          ["privacy.collectVersions"] = true,
          ["privacy.includeQueryString"] = true,
          ["retention.rawEventsDays"] = 365 * 3,
          ["retention.minuteRollupsDays"] = 7,
          ["retention.hourRollupsDays"] = 800,
          ["retention.dayRollupsDays"] = null,
        }),
    new("indexes", "dashboard-filters", "Dashboard filters",
        "Indexes source, country, device and page, and every pair of them, so filtered overview reports stay fast.",
        new Dictionary<string, object>
        {
          ["indexes.dimensions"] = DashboardIndexPreset.Dimensions,
          ["indexes.rollups"] = DashboardIndexPreset.RollupIndexes(),
        }),
    new("indexes", "no-indexes", "No indexes",
        "Fastest ingestion and least storage. Filtered reports read rollups and raw events.",
        new Dictionary<string, object>
        {
          ["indexes.dimensions"] = Array.Empty<string>(),
          ["indexes.rollups"] = Array.Empty<RollupIndexSettings>(),
        }),
  ];
}
