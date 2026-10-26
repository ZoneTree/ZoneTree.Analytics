using ZoneTree.Analytics;
using ZoneTree.Options;

// The persisted dashboard configuration. Every serializable engine and host option is here;
// extensions that are code (geolocator, user agent parser, filters, enrichers) stay in code.
// The [Setting] metadata drives the setup wizard and the admin forms (see SettingsSchema).
sealed class DashboardSettings
{
  [Section("General", Help = "Calendar, storage location and dashboard behavior.")]
  public GeneralSettings General { get; set; } = new();

  [Section("Sites", Help = "The websites this server collects analytics for.")]
  public SitesSettings Sites { get; set; } = new();

  [Section("Tracking", Help = "How page views and events are collected and cleaned.")]
  public TrackingSettings Tracking { get; set; } = new();

  [Section("Privacy", Help = "Visitor identification and data minimization.")]
  public PrivacySettings Privacy { get; set; } = new();

  [Section("Sessions", Help = "Visits, bounce rate, duration, entry and exit pages.")]
  public SessionSettings Sessions { get; set; } = new();

  [Section("Retention", Help = "How long each level of detail is kept. Older data is deleted.")]
  public RetentionSettings Retention { get; set; } = new();

  [Section("Rollups", Help = "Pre-aggregated reports that answer queries without scanning raw events.")]
  public RollupSettings Rollups { get; set; } = new();

  [Section("Indexes", Help = "Secondary indexes that speed up filtered reports.")]
  public IndexSettings Indexes { get; set; } = new();

  [Section("Ingestion", Help = "Event queue, workers and flushing.")]
  public IngestionSettings Ingestion { get; set; } = new();

  [Section("Queries", Help = "Limits that protect the server from expensive reports.")]
  public QuerySettings Queries { get; set; } = new();

  [Section("Storage", Help = "Raw event storage, partitions and ZoneTree store tuning.")]
  public StorageSettings Storage { get; set; } = new();

  [Section("Reporting API", Help = "Request and response limits of the reporting API.")]
  public ReportingSettings Reporting { get; set; } = new();

  [Section("Security", Help = "Dashboard sign-in sessions and sign-in protection.")]
  public SecuritySettings Security { get; set; } = new();
}

static class CoreDefaults
{
  public static readonly AnalyticsOptions Options = new();
}

sealed class GeneralSettings
{
  [Setting("Reporting time zone", Tier = SettingTier.Fixed, Editor = "timeZone",
      Help = "Days, weeks and months follow this IANA time zone. It is stored with the data and cannot change later.")]
  public string TimeZone { get; set; } = "UTC";

  [Setting("First day of the week", Help = "Where weekly reports start.")]
  public DayOfWeek FirstDayOfWeek { get; set; } = CoreDefaults.Options.FirstDayOfWeek;

  [Setting("Data directory", Tier = SettingTier.Fixed, Advanced = true,
      Help = "Where analytics data is stored. A relative path is resolved against the server's content root.")]
  public string DataDirectory { get; set; } = "data/analytics";

  [Setting("Auto-refresh", Tier = SettingTier.Live, Options = "0:Off|30:Every 30 seconds|60:Every minute|300:Every 5 minutes",
      Help = "How often open reports reload.")]
  public int RefreshSeconds { get; set; } = 30;
}

enum UnmatchedTraffic { PrimarySite, Ignore }

sealed class SiteSettings
{
  public string Id { get; set; }
  public string Name { get; set; }
  // Host names whose pages report to this site: exact, or *.example.com for subdomains.
  public List<string> Domains { get; set; } = [];
}

sealed class SitesSettings
{
  [Setting("Sites", Tier = SettingTier.Live, Editor = "sites")]
  public List<SiteSettings> Items { get; set; } = [];

  [Setting("Primary site", Editor = "primarySite",
      Help = "Receives events that name no site and answers reports that name none.")]
  public string PrimarySite { get; set; }

  [Setting("Traffic from other domains", Tier = SettingTier.Live,
      Help = "What happens to events from pages whose host matches no site's domains.")]
  public UnmatchedTraffic UnmatchedTraffic { get; set; } = UnmatchedTraffic.PrimarySite;

  [Setting("Record dashboard usage", Tier = SettingTier.Live,
      Help = "Track visits to this dashboard in a separate \"Dashboard usage\" site that only the administrator sees.")]
  public bool TrackDashboardUsage { get; set; }
}

sealed class TrackingSettings
{
  [Setting("Bot traffic", Help = "Discard bots, or keep them in separate bot reports.")]
  public BotTraffic Bots { get; set; } = BotTraffic.TrackSeparately;

  [Setting("Remove trailing slashes", Help = "Report /pricing/ and /pricing as one page.")]
  public bool TrimTrailingSlash { get; set; } = CoreDefaults.Options.Web.TrimTrailingSlash;

  [Setting("Lowercase page paths", Help = "Report /Pricing and /pricing as one page.")]
  public bool LowercasePaths { get; set; } = CoreDefaults.Options.Web.LowercasePaths;

  [Setting("Allowed custom events", Tier = SettingTier.Live, Editor = "tags",
      Help = "Event names the browser tracker may send. Empty accepts every name.")]
  public List<string> AllowedClientEvents { get; set; } = [];

  [Setting("Internal hosts", Advanced = true, Editor = "tags",
      Help = "Referrers from these hosts count as direct traffic, like referrers from the page's own host.")]
  public List<string> InternalHosts { get; set; } = [];

  [Setting("Referrer sources", Advanced = true, Editor = "map", KeyLabel = "Referrer host", ValueLabel = "Source name",
      Help = "Extra referrer host to source mappings, for example news.ycombinator.com → Hacker News.")]
  public Dictionary<string, string> ReferrerSources { get; set; } = [];

  [Setting("Maximum event size", Tier = SettingTier.Live, Advanced = true, Unit = "bytes", Min = 256, Max = 1048576,
      Help = "Larger tracker requests are rejected.")]
  public int MaxPayloadBytes { get; set; } = 8 * 1024;
}

sealed class PrivacySettings
{
  [Setting("Visitor identification",
      Help = "Daily rotating hash needs no cookies and cannot follow visitors across days. Persistent hash recognizes returning visitors.")]
  public VisitorIdentification VisitorIdentification { get; set; } = CoreDefaults.Options.Privacy.VisitorIdentification;

  [Setting("Location precision", Help = "The most precise location kept, when a geolocator is configured.")]
  public GeoPrecision GeoPrecision { get; set; } = CoreDefaults.Options.Privacy.GeoPrecision;

  [Setting("Browser, OS and device", Help = "Derive browser, operating system and device type from the user agent.")]
  public bool CollectUserAgentDetails { get; set; } = CoreDefaults.Options.Privacy.CollectUserAgentDetails;

  [Setting("Browser and OS versions", Help = "Also keep version numbers.")]
  public bool CollectVersions { get; set; } = CoreDefaults.Options.Privacy.CollectVersions;

  [Setting("Keep query strings", Help = "Keep ?query parameters in page URLs. They can contain personal data.")]
  public bool IncludeQueryString { get; set; } = CoreDefaults.Options.Privacy.IncludeQueryString;

  [Setting("Allow only listed custom dimensions", Advanced = true,
      Help = "When on, custom dimensions not listed below are discarded.")]
  public bool RestrictDimensions { get; set; }

  [Setting("Allowed custom dimensions", Advanced = true, Editor = "tags")]
  public List<string> AllowedDimensions { get; set; } = [];

  [Setting("Denied custom dimensions", Advanced = true, Editor = "tags", Help = "Always discarded.")]
  public List<string> DeniedDimensions { get; set; } = [];

  [Setting("Custom dimensions per event", Advanced = true, Min = 0, Max = 1000)]
  public int MaxCustomDimensions { get; set; } = CoreDefaults.Options.Privacy.MaxCustomDimensions;

  [Setting("Custom values per event", Advanced = true, Min = 0, Max = 1000)]
  public int MaxCustomValues { get; set; } = CoreDefaults.Options.Privacy.MaxCustomValues;

  [Setting("Maximum value length", Advanced = true, Unit = "characters", Min = 16, Max = 65536)]
  public int MaxValueLength { get; set; } = CoreDefaults.Options.Privacy.MaxValueLength;
}

sealed class SessionSettings
{
  [Setting("Track sessions", Help = "Required for sessions, bounce rate, visit duration, entry and exit pages.")]
  public bool Enabled { get; set; } = CoreDefaults.Options.Sessions.Enabled;

  [Setting("Session timeout", Unit = "minutes", Min = 1, Max = 1440, Help = "Inactivity that ends a visit.")]
  public int TimeoutMinutes { get; set; } = (int)CoreDefaults.Options.Sessions.Timeout.TotalMinutes;

  [Setting("Custom session dimensions", Advanced = true, Editor = "tags",
      Help = "Custom dimensions captured from a visit's first event, for example an experiment variant.")]
  public List<string> Dimensions { get; set; } = [];

  [Setting("Cached sessions per worker", Advanced = true, Min = 1, Max = 100000000)]
  public int MaxCachedSessions { get; set; } = CoreDefaults.Options.Sessions.MaxCachedSessions;
}

sealed class RetentionSettings
{
  [Setting("Raw events", Unit = "days", Min = 1, Max = 36500, EmptyLabel = "Keep forever",
      Help = "Raw events answer ad-hoc queries, real-time counts and erasure requests.")]
  public int? RawEventsDays { get; set; } = 365;

  [Setting("Minute rollups", Unit = "days", Min = 1, Max = 36500, EmptyLabel = "Keep forever")]
  public int? MinuteRollupsDays { get; set; } = 2;

  [Setting("Hourly rollups", Unit = "days", Min = 1, Max = 36500, EmptyLabel = "Keep forever")]
  public int? HourRollupsDays { get; set; } = 400;

  [Setting("Daily rollups", Unit = "days", Min = 1, Max = 36500, EmptyLabel = "Keep forever",
      Help = "Daily rollups answer week, month and year reports.")]
  public int? DayRollupsDays { get; set; }

  [Setting("Retention check interval", Advanced = true, Unit = "minutes", Min = 1, Max = 10080, EmptyLabel = "Never (manual)")]
  public int? CheckIntervalMinutes { get; set; } = 60;
}

enum RollupGranularity { Minute, Hour, Day }

sealed class RollupDefinitionSettings
{
  public List<string> Dimensions { get; set; } = [];
  // Null counts every event.
  public List<string> Events { get; set; }
}

sealed class ValueAggregateSettings
{
  public string Value { get; set; }
  public bool Percentiles { get; set; }
  public bool FirstAndLast { get; set; }
}

sealed class RollupSettings
{
  [Setting("Granularities", Editor = "multi", Help = "Week, month and year reports are computed from daily rollups.")]
  public List<RollupGranularity> Granularities { get; set; } = [RollupGranularity.Hour, RollupGranularity.Day];

  [Setting("Rollup definitions", Editor = "rollups")]
  public List<RollupDefinitionSettings> Definitions { get; set; } =
      [.. RollupDefinition.WebDefaults().Select(definition => new RollupDefinitionSettings
      {
        Dimensions = [.. definition.Dimensions],
        Events = definition.Events?.ToList(),
      })];

  [Setting("Value aggregates", Editor = "valueAggregates")]
  public List<ValueAggregateSettings> ValueAggregates { get; set; } = [new() { Value = "load_time", Percentiles = true }];

  [Setting("Distinct-count dimensions", Advanced = true, Editor = "tags",
      Help = "Dimensions whose distinct values are counted in every rollup row. Each costs a sketch per row.")]
  public List<string> DistinctDimensions { get; set; } = [];

  [Setting("Visitor sketch precision", Tier = SettingTier.Fixed, Advanced = true, Min = 8, Max = 18,
      Help = "Unique visitor counts are exact up to 2^precision / 4 per row. Stored with the data and cannot change later.")]
  public int HyperLogLogPrecision { get; set; } = CoreDefaults.Options.Rollups.HyperLogLogPrecision;
}

sealed class RollupIndexSettings
{
  public string Dimension { get; set; }
  public List<string> GroupDimensions { get; set; } = [];
}

sealed class IndexSettings
{
  [Setting("Dimension indexes", Editor = "dimensionIndexes")]
  public List<string> Dimensions { get; set; } = [.. DashboardIndexPreset.Dimensions];

  [Setting("Rollup indexes", Editor = "rollupIndexes")]
  public List<RollupIndexSettings> Rollups { get; set; } = DashboardIndexPreset.RollupIndexes();

  [Setting("Dimension index partition size", Advanced = true, Unit = "records", Min = 1000, Max = 1000000000)]
  public long DimensionRecordsPerPartition { get; set; } = CoreDefaults.Options.Storage.DimensionIndexes.RecordsPerPartition;

  [Setting("Rollup index partition size", Advanced = true, Unit = "records", Min = 1000, Max = 1000000000)]
  public long RollupRecordsPerPartition { get; set; } = CoreDefaults.Options.Storage.RollupIndexes.RecordsPerPartition;
}

// The overview's filters, covered in both directions for every pair.
static class DashboardIndexPreset
{
  public static readonly string[] Dimensions = ["source", "country", "device", "page"];

  public static List<RollupIndexSettings> RollupIndexes() =>
      [.. from dimension in Dimensions
          from other in Dimensions
          where dimension != other
          select new RollupIndexSettings { Dimension = dimension, GroupDimensions = [other] }];
}

sealed class IngestionSettings
{
  [Setting("Ingestion workers", Advanced = true, Min = 1, Max = 64)]
  public int WorkerCount { get; set; } = CoreDefaults.Options.Ingestion.WorkerCount;

  [Setting("Rollup writers", Advanced = true, Min = 1, Max = 64)]
  public int RollupWriterCount { get; set; } = CoreDefaults.Options.Ingestion.RollupWriterCount;

  [Setting("Queue capacity", Advanced = true, Unit = "events", Min = 1, Max = 100000000)]
  public int QueueCapacity { get; set; } = CoreDefaults.Options.Ingestion.QueueCapacity;

  [Setting("When the queue is full", Advanced = true, Help = "Drop new events, or make trackers wait.")]
  public QueueFullBehavior QueueFullBehavior { get; set; } = CoreDefaults.Options.Ingestion.QueueFullBehavior;

  [Setting("Flush interval", Advanced = true, Unit = "seconds", Min = 0.1, Max = 3600)]
  public double FlushIntervalSeconds { get; set; } = CoreDefaults.Options.Ingestion.FlushInterval.TotalSeconds;

  [Setting("Buffered rollup rows", Advanced = true, Unit = "rows", Min = 1, Max = 100000000)]
  public int MaxBufferedRollupRows { get; set; } = CoreDefaults.Options.Ingestion.MaxBufferedRollupRows;

  [Setting("Accepted clock skew", Advanced = true, Unit = "minutes", Min = 0, Max = 10080,
      Help = "Events timestamped further in the future are rejected.")]
  public int MaxFutureSkewMinutes { get; set; } = (int)CoreDefaults.Options.Ingestion.MaxFutureSkew.TotalMinutes;
}

sealed class QuerySettings
{
  [Setting("Query timeout", Unit = "seconds", Min = 1, Max = 3600, EmptyLabel = "No limit")]
  public int? TimeoutSeconds { get; set; } = 30;

  [Setting("Parallel scan threads", Advanced = true, Min = 0, Max = 1024, Help = "0 uses one per processor.")]
  public int MaxDegreeOfParallelism { get; set; }

  [Setting("Group-by dimensions", Advanced = true, Min = 1, Max = 64)]
  public int MaxGroupBy { get; set; } = CoreDefaults.Options.Queries.MaxGroupBy;

  [Setting("Groups per query", Advanced = true, Min = 1, Max = 100000000)]
  public int MaxGroups { get; set; } = CoreDefaults.Options.Queries.MaxGroups;

  [Setting("Time buckets per query", Advanced = true, Min = 1, Max = 100000000)]
  public int MaxTimeBuckets { get; set; } = CoreDefaults.Options.Queries.MaxTimeBuckets;

  [Setting("Filled rows per query", Advanced = true, Min = 1, Max = 1000000000)]
  public int MaxFilledRows { get; set; } = CoreDefaults.Options.Queries.MaxFilledRows;
}

// The ZoneTree settings an administrator may tune. The B+tree lock mode, B+tree node sizes and
// Bloom filters are not settings: they keep the engine's defaults.
sealed class StoreSettings
{
  [Setting("Write-ahead log", Help = "Async compressed is ZoneTree's default.")]
  public WriteAheadLogMode WriteAheadLogMode { get; set; } = WriteAheadLogMode.AsyncCompressed;

  [Setting("Mutable segment size", Unit = "records", Min = 1000, Max = 100000000, EmptyLabel = "ZoneTree default")]
  public int? MutableSegmentMaxItemCount { get; set; }

  [Setting("Disk segment size", Unit = "records", Min = 10000, Max = 1000000000, EmptyLabel = "ZoneTree default")]
  public int? DiskSegmentMaxItemCount { get; set; }

  [Setting("Single segment garbage collection", EmptyLabel = "ZoneTree default")]
  public bool? EnableSingleSegmentGarbageCollection { get; set; }

  public static StoreSettings From(StoreOptions store) => new()
  {
    WriteAheadLogMode = store.WriteAheadLogMode,
    MutableSegmentMaxItemCount = store.MutableSegmentMaxItemCount,
    DiskSegmentMaxItemCount = store.DiskSegmentMaxItemCount,
    EnableSingleSegmentGarbageCollection = store.EnableSingleSegmentGarbageCollection,
  };
}

sealed class StorageSettings
{
  [Setting("Store raw events", Help = "Without raw events only rollups are kept: ad-hoc queries, dimension indexes and real-time counts are unavailable.")]
  public bool StoreRawEvents { get; set; } = CoreDefaults.Options.Storage.StoreRawEvents;

  [Setting("Write-ahead log directory", Tier = SettingTier.Fixed, Advanced = true, EmptyLabel = "Next to the data",
      Help = "A separate directory for write-ahead logs, for example on a faster disk. Cannot change later.")]
  public string WriteAheadLogDirectory { get; set; }

  [Setting("Close idle partitions after", Advanced = true, Unit = "minutes", Min = 1, Max = 10080)]
  public int PartitionIdleTimeoutMinutes { get; set; } = (int)CoreDefaults.Options.Storage.PartitionIdleTimeout.TotalMinutes;

  [Setting("Raw event partition size", Advanced = true, Unit = "records", Min = 1000, Max = 1000000000)]
  public long RawEventsPerPartition { get; set; } = CoreDefaults.Options.Storage.Partitioning.RawEvents;

  [Setting("Minute rollup partition size", Advanced = true, Unit = "records", Min = 1000, Max = 1000000000)]
  public long MinuteRollupsPerPartition { get; set; } = CoreDefaults.Options.Storage.Partitioning.MinuteRollups;

  [Setting("Hourly rollup partition size", Advanced = true, Unit = "records", Min = 1000, Max = 1000000000)]
  public long HourRollupsPerPartition { get; set; } = CoreDefaults.Options.Storage.Partitioning.HourRollups;

  [Setting("Daily rollup partition size", Advanced = true, Unit = "records", Min = 1000, Max = 1000000000)]
  public long DayRollupsPerPartition { get; set; } = CoreDefaults.Options.Storage.Partitioning.DayRollups;

  [Setting("Raw event store", Advanced = true)]
  public StoreSettings RawEventStore { get; set; } = StoreSettings.From(CoreDefaults.Options.Storage.RawEvents);

  [Setting("Rollup store", Advanced = true)]
  public StoreSettings RollupStore { get; set; } = StoreSettings.From(CoreDefaults.Options.Storage.Rollups);

  [Setting("Session store", Advanced = true)]
  public StoreSettings SessionStore { get; set; } = StoreSettings.From(CoreDefaults.Options.Storage.Sessions);

  [Setting("Dimension index store", Advanced = true)]
  public StoreSettings DimensionIndexStore { get; set; } = StoreSettings.From(CoreDefaults.Options.Storage.DimensionIndexes.Store);

  [Setting("Rollup index store", Advanced = true)]
  public StoreSettings RollupIndexStore { get; set; } = StoreSettings.From(CoreDefaults.Options.Storage.RollupIndexes.Store);
}

sealed class ReportingSettings
{
  [Setting("Concurrent reports", Tier = SettingTier.Restart, Advanced = true, Min = 1, Max = 1024,
      Help = "Reports running at once. Further requests receive 503.")]
  public int MaxConcurrentRequests { get; set; } = 4;

  [Setting("Maximum request size", Tier = SettingTier.Live, Advanced = true, Unit = "bytes", Min = 1024, Max = 16777216)]
  public int MaxRequestBytes { get; set; } = 64 * 1024;

  [Setting("Maximum response size", Tier = SettingTier.Live, Advanced = true, Unit = "bytes", Min = 65536, Max = 1073741824)]
  public int MaxResponseBytes { get; set; } = 16 * 1024 * 1024;
}

sealed class SecuritySettings
{
  [Setting("Sign out after inactivity", Tier = SettingTier.Live, Unit = "hours", Min = 1, Max = 8760)]
  public int SessionIdleHours { get; set; } = 7 * 24;

  [Setting("Maximum session length", Tier = SettingTier.Live, Unit = "days", Min = 1, Max = 365,
      Help = "Users sign in again after this long, even when active.")]
  public int SessionLifetimeDays { get; set; } = 30;

  [Setting("Failed sign-ins before a pause", Tier = SettingTier.Live, Min = 1, Max = 100)]
  public int MaxFailedSignIns { get; set; } = 5;

  [Setting("Sign-in pause", Tier = SettingTier.Live, Unit = "minutes", Min = 1, Max = 1440,
      Help = "How long sign-in is paused for a user name and address after repeated failures.")]
  public int SignInLockoutMinutes { get; set; } = 15;

  [Setting("Keep the audit log for", Tier = SettingTier.Live, Unit = "days", Min = 1, Max = 3650,
      Help = "Older entries are removed. The log also keeps at most the newest 100,000 entries.")]
  public int AuditRetentionDays { get; set; } = 90;
}
