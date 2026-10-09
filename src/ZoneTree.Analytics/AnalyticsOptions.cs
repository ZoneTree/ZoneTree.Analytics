using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Analytics.Web;
using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.Analytics;

/// <summary>
/// Configuration of an <see cref="AnalyticsEngine"/>.
/// </summary>
public sealed class AnalyticsOptions
{
  /// <summary>
  /// The root directory of all analytics data. Default is "data/analytics".
  /// </summary>
  public string DataDirectory { get; set; } = "data/analytics";

  /// <summary>
  /// The site used for events and queries that do not specify one. Default is "default".
  /// </summary>
  public string DefaultSite { get; set; } = "default";

  /// <summary>
  /// The reporting time zone. Daily rollups and day, week, month and year
  /// query intervals use calendar boundaries of this time zone.
  /// It is persisted when the data directory is created and cannot be changed afterwards.
  /// Default is UTC.
  /// </summary>
  public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

  /// <summary>
  /// The first day of week for weekly query intervals. Default is Monday.
  /// </summary>
  public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Monday;

  /// <summary>
  /// Filters that decide whether an event is tracked. All must pass. They run inside
  /// <see cref="AnalyticsEngine.Track(AnalyticsEvent)"/> on the calling thread.
  /// Create one from a lambda with <see cref="EventFilter.Create"/>.
  /// </summary>
  public List<IEventFilter> EventFilters { get; } = [];

  /// <summary>
  /// Enrichers that add, change or remove dimensions after the built-in enrichment, in order.
  /// Create one from a lambda with <see cref="EventEnricher.Create"/> or use
  /// <see cref="EventEnricher.NormalizePage"/>.
  /// </summary>
  public List<IEventEnricher> Enrichers { get; } = [];

  /// <summary>
  /// Ingestion pipeline options.
  /// </summary>
  public IngestionOptions Ingestion { get; set; } = new();

  /// <summary>
  /// Session tracking options.
  /// </summary>
  public SessionOptions Sessions { get; set; } = new();

  /// <summary>
  /// Pre-aggregation options.
  /// </summary>
  public RollupOptions Rollups { get; set; } = new();

  /// <summary>
  /// Data retention options.
  /// </summary>
  public RetentionOptions Retention { get; set; } = new();

  /// <summary>
  /// Privacy options.
  /// </summary>
  public PrivacyOptions Privacy { get; set; } = new();

  /// <summary>
  /// Web enrichment options.
  /// </summary>
  public WebOptions Web { get; set; } = new();

  /// <summary>
  /// Storage options.
  /// </summary>
  public StorageOptions Storage { get; set; } = new();

  /// <summary>
  /// Query options.
  /// </summary>
  public QueryOptions Queries { get; set; } = new();

  internal void Validate()
  {
    if (string.IsNullOrWhiteSpace(DataDirectory))
      throw new ArgumentException("DataDirectory is required.");
    if (string.IsNullOrWhiteSpace(DefaultSite))
      throw new ArgumentException("DefaultSite is required.");
    if (TimeZone == null)
      throw new ArgumentException("TimeZone is required.");
    Ingestion ??= new();
    Sessions ??= new();
    Rollups ??= new();
    Retention ??= new();
    Privacy ??= new();
    Web ??= new();
    Storage ??= new();
    if (Ingestion.QueueCapacity < 1)
      throw new ArgumentException("Ingestion.QueueCapacity must be positive.");
    if (Ingestion.WorkerCount < 1 || Ingestion.WorkerCount > 64)
      throw new ArgumentException("Ingestion.WorkerCount must be between 1 and 64.");
    if (Ingestion.FlushInterval <= TimeSpan.Zero)
      throw new ArgumentException("Ingestion.FlushInterval must be positive.");
    if (Ingestion.MaxBufferedRollupRows < 1)
      throw new ArgumentException("Ingestion.MaxBufferedRollupRows must be positive.");
    if (Sessions.Timeout <= TimeSpan.Zero)
      throw new ArgumentException("Sessions.Timeout must be positive.");
    HyperLogLog.ValidatePrecision(Rollups.HyperLogLogPrecision);
    Rollups.Definitions ??= [];
    var dimensionSets = new HashSet<string>(StringComparer.Ordinal);
    foreach (var definition in Rollups.Definitions)
    {
      if (definition == null)
        throw new ArgumentException("Rollup definitions cannot contain null.");
      if (!dimensionSets.Add(definition.Id))
        throw new ArgumentException(
            $"Duplicate rollup definition for dimensions '{definition.Id}'.");
    }
    Storage.Partitioning ??= new();
    Storage.Partitioning.Validate(Retention, Storage.StoreRawEvents, Rollups.Granularities);
    if (Privacy.MaxCustomDimensions < 0)
      throw new ArgumentException("Privacy.MaxCustomDimensions cannot be negative.");
    if (Privacy.MaxCustomMetrics < 0)
      throw new ArgumentException("Privacy.MaxCustomMetrics cannot be negative.");
    Queries ??= new();
    if (Queries.Timeout <= TimeSpan.Zero)
      throw new ArgumentException("Queries.Timeout must be positive, or null for no limit.");
    ValidateSessionDimensions();
    if (Privacy.MaxValueLength < 16)
      throw new ArgumentException("Privacy.MaxValueLength must be at least 16.");
    if (Privacy.VisitorIdSecret != null && Privacy.VisitorIdSecret.Length < 16)
      throw new ArgumentException("Privacy.VisitorIdSecret must be at least 16 bytes.");
  }

  /// <summary>
  /// At most this many custom session-level dimensions are allowed.
  /// </summary>
  internal const int MaxSessionDimensions = 16;

  void ValidateSessionDimensions()
  {
    var custom = Sessions.Dimensions;
    if (custom.Count > MaxSessionDimensions)
      throw new ArgumentException($"At most {MaxSessionDimensions} custom session dimensions are supported.");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var name in custom)
    {
      if (string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.Contains(',') ||
          name.Any(char.IsControl) || name.Length > 128 || name[0] == '$')
        throw new ArgumentException($"'{name}' is not a valid session dimension name.");
      if (StandardDimensions.SessionLevel.Contains(name))
        throw new ArgumentException($"'{name}' is already a standard session-level dimension.");
      if (name is StandardDimensions.Page or StandardDimensions.Hostname or StandardDimensions.EntryPage ||
          StandardDimensions.BotLevel.Contains(name))
        throw new ArgumentException($"'{name}' is maintained by the engine and cannot be a session dimension.");
      if (!seen.Add(name))
        throw new ArgumentException($"Duplicate session dimension '{name}'.");
      if (Privacy.DeniedDimensions?.Contains(name) == true ||
          (Privacy.AllowedDimensions != null && !Privacy.AllowedDimensions.Contains(name)))
        throw new ArgumentException(
            $"The session dimension '{name}' is excluded by Privacy.AllowedDimensions or Privacy.DeniedDimensions.");
    }
  }
}

/// <summary>
/// Ingestion pipeline options.
/// </summary>
public sealed class IngestionOptions
{
  /// <summary>
  /// Maximum number of queued events per worker. Default is 100,000.
  /// </summary>
  public int QueueCapacity { get; set; } = 100_000;

  /// <summary>
  /// Number of background ingestion workers. Events are routed to workers by
  /// visitor, so each visitor's events are processed in order. Default is 1,
  /// which handles well over a hundred thousand events per second.
  /// </summary>
  public int WorkerCount { get; set; } = 1;

  /// <summary>
  /// How often buffered aggregates and session states are written to storage.
  /// Rollup-based queries lag behind ingestion by at most this interval.
  /// Default is 2 seconds.
  /// </summary>
  public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Forces a flush when a worker buffers more distinct rollup rows than this.
  /// Default is 100,000.
  /// </summary>
  public int MaxBufferedRollupRows { get; set; } = 100_000;

  /// <summary>
  /// Behavior of <see cref="AnalyticsEngine.Track"/> when the queue is full.
  /// Default is <see cref="QueueFullBehavior.Drop"/>, so request threads never block.
  /// </summary>
  public QueueFullBehavior QueueFullBehavior { get; set; } = QueueFullBehavior.Drop;

  /// <summary>
  /// Events with a timestamp further in the future than this are rejected.
  /// Default is 1 hour.
  /// </summary>
  public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Behavior when the ingestion queue is full.
/// </summary>
public enum QueueFullBehavior
{
  /// <summary>The event is dropped and counted in <see cref="AnalyticsStatistics.EventsDropped"/>.</summary>
  Drop,

  /// <summary>The caller blocks until queue space is available.</summary>
  Wait,
}

/// <summary>
/// Query options.
/// </summary>
public sealed class QueryOptions
{
  /// <summary>
  /// The longest a query or funnel may run before it is cancelled with an
  /// <see cref="AnalyticsQueryTimeoutException"/>. It protects the host application from
  /// expensive ad-hoc queries, for example long raw event scans requested through a reporting
  /// API. Null means no limit. Default is 30 seconds.
  /// </summary>
  public TimeSpan? Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Session tracking options.
/// </summary>
public sealed class SessionOptions
{
  /// <summary>
  /// Enables sessions, bounce rate, session duration, entry and exit pages.
  /// Default is true.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Inactivity timeout that ends a session. Default is 30 minutes.
  /// </summary>
  public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);

  /// <summary>
  /// Maximum number of sessions cached in memory per worker. Evicted sessions
  /// remain available in storage. Default is 250,000.
  /// </summary>
  public int MaxCachedSessions { get; set; } = 250_000;

  /// <summary>
  /// Custom dimensions that are session-level, for example an experiment variant or a plan.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Like source, country or device, a session-level dimension is captured from the first event
  /// of a session and attached to every event of that session; values on later events of the
  /// session are ignored. Session metrics (sessions, bounce rate, duration) and entrances and
  /// exits can then be grouped and filtered by it, and enrichers can set it on the first event.
  /// </para>
  /// <para>
  /// Track the value as an event dimension, for example
  /// <c>Dimensions = { ["ab_variant"] = "B" }</c>, and add a <see cref="RollupDefinition"/> for it
  /// to answer its reports from rollups. Dimensions can be added or removed at any time;
  /// a dimension added later applies to sessions that start afterwards.
  /// </para>
  /// </remarks>
  public List<string> Dimensions { get; } = [];
}

/// <summary>
/// Pre-aggregation options.
/// </summary>
public sealed class RollupOptions
{
  /// <summary>
  /// Rollup granularities. Default is <see cref="Granularity.Hour"/> | <see cref="Granularity.Day"/>.
  /// Week, month and year intervals are computed from daily rollups.
  /// </summary>
  public Granularity Granularities { get; set; } = Granularity.Hour | Granularity.Day;

  /// <summary>
  /// The dimension sets to pre-aggregate. A query is answered from rollups when the
  /// union of its group-by and filter dimensions equals one of these sets;
  /// otherwise it is answered by scanning raw events.
  /// Default is <see cref="RollupDefinition.WebDefaults"/>.
  /// </summary>
  public List<RollupDefinition> Definitions { get; set; } = RollupDefinition.WebDefaults();

  /// <summary>
  /// Precision of unique visitor sketches. Distinct counts up to 2^precision / 4
  /// are exact; larger ones have a standard error of about 1.04 / sqrt(2^precision).
  /// It is persisted when the data directory is created and cannot be changed afterwards.
  /// Default is 14: exact up to 4,096 visitors per row, then about 0.8% error.
  /// </summary>
  public int HyperLogLogPrecision { get; set; } = HyperLogLog.DefaultPrecision;
}

/// <summary>
/// A pre-aggregated dimension set.
/// </summary>
public sealed class RollupDefinition
{
  /// <summary>
  /// Creates a rollup over the given dimensions. No dimensions means totals.
  /// </summary>
  public RollupDefinition(params string[] dimensions)
  {
    dimensions ??= [];
    var sorted = dimensions
        .Select(x => x?.Trim())
        .Where(x => !string.IsNullOrEmpty(x))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();
    if (sorted.Length != dimensions.Length)
      throw new ArgumentException("Rollup dimensions must be distinct and non-empty.");
    foreach (var dimension in sorted)
    {
      if (dimension.Any(c => char.IsControl(c) || c == ','))
        throw new ArgumentException($"Invalid dimension name '{dimension}'.");
    }
    Dimensions = sorted;
    Id = string.Join(",", sorted);
  }

  /// <summary>
  /// The dimensions in canonical (ordinal) order.
  /// </summary>
  public IReadOnlyList<string> Dimensions { get; }

  /// <summary>
  /// The canonical identifier: the dimension names joined by commas.
  /// </summary>
  public string Id { get; }

  /// <summary>
  /// Restricts the rollup to the given event names. Null means every tracked event; events
  /// that do not carry a dimension are counted under a missing (null) value.
  /// </summary>
  public IReadOnlyCollection<string> Events { get; init; }

  /// <summary>
  /// The default web analytics rollups: totals, page, hostname, entry page, every
  /// session-level dimension except browser and OS versions, and bot name and category
  /// for bot events.
  /// </summary>
  public static List<RollupDefinition> WebDefaults()
  {
    var list = new List<RollupDefinition>
    {
      new(),
      new(StandardDimensions.Page),
      new(StandardDimensions.Hostname),
      new(StandardDimensions.EntryPage),
      new(StandardDimensions.Bot) { Events = [StandardEvents.Bot] },
      new(StandardDimensions.BotCategory) { Events = [StandardEvents.Bot] },
      new(StandardDimensions.Event) { Events = [StandardEvents.Bot] },
      new(StandardDimensions.Bot, StandardDimensions.BotCategory) { Events = [StandardEvents.Bot] },
    };
    foreach (var dimension in StandardDimensions.SessionLevel)
    {
      if (dimension == StandardDimensions.BrowserVersion ||
          dimension == StandardDimensions.OperatingSystemVersion)
        continue;
      list.Add(new(dimension));
    }
    return list;
  }

  /// <inheritdoc/>
  public override string ToString() => Id.Length == 0 ? "(totals)" : Id;
}

/// <summary>
/// Data retention options. Null means keep forever.
/// Retention removes whole storage partitions, so data is kept for at most
/// one partition period longer than configured.
/// </summary>
public sealed class RetentionOptions
{
  /// <summary>
  /// Retention of raw events. Default is 90 days.
  /// </summary>
  public TimeSpan? RawEvents { get; set; } = TimeSpan.FromDays(90);

  /// <summary>
  /// Retention of minute rollups. Default is 2 days.
  /// </summary>
  public TimeSpan? MinuteRollups { get; set; } = TimeSpan.FromDays(2);

  /// <summary>
  /// Retention of hourly rollups. Default is 400 days.
  /// </summary>
  public TimeSpan? HourRollups { get; set; } = TimeSpan.FromDays(400);

  /// <summary>
  /// Retention of daily rollups. Default is forever.
  /// </summary>
  public TimeSpan? DayRollups { get; set; }

  /// <summary>
  /// How often retention is applied automatically. Default is 1 hour.
  /// Set to null to apply retention only through <see cref="AnalyticsEngine.ApplyRetention"/>.
  /// </summary>
  public TimeSpan? CheckInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// How visitors are identified.
/// </summary>
public enum VisitorIdentification
{
  /// <summary>
  /// Explicit visitor ids are hashed with a persistent secret. Anonymous visitors are
  /// identified by a hash of site, IP address and user agent with a random salt that
  /// rotates every UTC day and is then destroyed. No cookies are required and visitors
  /// cannot be followed across days. This is the default.
  /// </summary>
  DailyRotatingHash,

  /// <summary>
  /// Like <see cref="DailyRotatingHash"/> but the anonymous hash uses a persistent secret,
  /// so anonymous visitors are recognized across days. Less private.
  /// </summary>
  PersistentHash,

  /// <summary>
  /// Only explicit <see cref="AnalyticsEvent.VisitorId"/> values identify visitors.
  /// </summary>
  ExplicitOnly,

  /// <summary>
  /// Visitors are not identified. Unique visitors and sessions are not available.
  /// </summary>
  None,
}

/// <summary>
/// Geolocation precision to keep.
/// </summary>
public enum GeoPrecision
{
  /// <summary>No geography is stored.</summary>
  None,

  /// <summary>Country only.</summary>
  Country,

  /// <summary>Country and region.</summary>
  Region,

  /// <summary>Country, region and city.</summary>
  City,
}

/// <summary>
/// Privacy options.
/// </summary>
public sealed class PrivacyOptions
{
  /// <summary>
  /// Visitor identification strategy. Default is <see cref="VisitorIdentification.DailyRotatingHash"/>.
  /// </summary>
  public VisitorIdentification VisitorIdentification { get; set; } =
      VisitorIdentification.DailyRotatingHash;

  /// <summary>
  /// The secret key (at least 16 bytes) used to hash explicit visitor ids. When null,
  /// a random secret is generated and stored in the data directory. Supply it from a
  /// secret store to keep it away from the data files.
  /// </summary>
  public byte[] VisitorIdSecret { get; set; }

  /// <summary>
  /// Geolocation precision. Default is <see cref="GeoPrecision.City"/>.
  /// </summary>
  public GeoPrecision GeoPrecision { get; set; } = GeoPrecision.City;

  /// <summary>
  /// Derives browser, OS and device dimensions from the user agent. Default is true.
  /// </summary>
  public bool CollectUserAgentDetails { get; set; } = true;

  /// <summary>
  /// Also stores browser and OS versions. Default is false.
  /// </summary>
  public bool CollectVersions { get; set; }

  /// <summary>
  /// Keeps the query string in the page dimension. Query strings often contain
  /// personal data, so the default is false. UTM parameters are always extracted.
  /// </summary>
  public bool IncludeQueryString { get; set; }

  /// <summary>
  /// When set, only these custom dimensions are kept. Null keeps all.
  /// </summary>
  public ISet<string> AllowedDimensions { get; set; }

  /// <summary>
  /// Custom dimensions that are always discarded.
  /// </summary>
  public ISet<string> DeniedDimensions { get; set; }

  /// <summary>
  /// Maximum number of custom dimensions kept per event. Default is 32.
  /// </summary>
  public int MaxCustomDimensions { get; set; } = 32;

  /// <summary>
  /// Maximum number of custom metrics (numeric values) kept per event. Default is 32.
  /// </summary>
  public int MaxCustomMetrics { get; set; } = 32;

  /// <summary>
  /// Maximum length of names and values. Longer values are truncated. Default is 512.
  /// </summary>
  public int MaxValueLength { get; set; } = 512;
}

/// <summary>
/// Storage options.
/// </summary>
public sealed class StorageOptions
{
  /// <summary>
  /// Stores every enriched event. Raw events answer ad-hoc queries over any dimension
  /// combination, real-time visitor counts and visitor erasure requests.
  /// When disabled, only rollups are kept. Default is true.
  /// </summary>
  public bool StoreRawEvents { get; set; } = true;

  /// <summary>
  /// How many ZoneTree partitions each data tier is split into over time. It is persisted when
  /// the data directory is created and cannot be changed afterwards.
  /// </summary>
  public PartitioningOptions Partitioning { get; set; } = new();

  /// <summary>
  /// The ZoneTree write-ahead-log mode of every store. Default is
  /// <see cref="WriteAheadLogMode.AsyncCompressed"/>.
  /// </summary>
  public WriteAheadLogMode WriteAheadLogMode { get; set; } = WriteAheadLogMode.AsyncCompressed;

  /// <summary>
  /// Mutable segment size of raw event partitions: how many recent events are held in
  /// memory before they move toward disk. Default is 50,000.
  /// </summary>
  public int RawEventsMutableSegmentMaxItemCount { get; set; } = 50_000;

  /// <summary>
  /// Mutable segment size of rollup partitions. Rollup rows carry visitor sketches, so the
  /// default is kept small: 20,000. Rows that are still being updated are rewritten into
  /// the mutable segment, so lowering this mainly moves cold rows to disk sooner.
  /// </summary>
  public int RollupsMutableSegmentMaxItemCount { get; set; } = 20_000;

  /// <summary>
  /// Partitions that were not used for this long are closed. Default is 10 minutes.
  /// </summary>
  public TimeSpan PartitionIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

  /// <summary>
  /// Optional hook to tune ZoneTree disk segment options of every store.
  /// </summary>
  public Action<DiskSegmentOptions> ConfigureDiskSegments { get; set; }

  /// <summary>
  /// Optional hook to tune ZoneTree write-ahead-log options of every store.
  /// </summary>
  public Action<WriteAheadLogOptions> ConfigureWriteAheadLog { get; set; }

  /// <summary>
  /// Optional hook to tune the ZoneTree maintainer of every data store (raw event and rollup
  /// partitions and the session store), for example merge thresholds or block cache cleanup.
  /// It runs when a store or partition is opened.
  /// </summary>
  public Action<IMaintainer> ConfigureMaintainer { get; set; }

  /// <summary>
  /// The logger used by the engine and its ZoneTree stores.
  /// Default is a console logger at warning level.
  /// </summary>
  public ILogger Logger { get; set; }
}

/// <summary>
/// Time partitioning of each data tier. Every partition is a separate ZoneTree.
/// </summary>
/// <remarks>
/// <para>
/// Retention drops whole partitions once they end before the retention cutoff, so data is kept
/// for up to one partition period longer than its retention. Smaller partitions make retention
/// more precise, keep individual trees and their merges smaller and make drops cheaper; larger
/// partitions mean fewer trees, files and directories, and fewer trees per long-range query.
/// </para>
/// <para>
/// Validation rejects a partition period longer than the retention of its tier when a shorter
/// period exists, and daily partitions of daily rollups, which would hold a single bucket each.
/// Partitioning is persisted when the data directory is created; reopening with different
/// values throws <see cref="AnalyticsConfigurationException"/>.
/// </para>
/// </remarks>
public sealed class PartitioningOptions
{
  /// <summary>
  /// Partition period of raw events (UTC). Default is <see cref="PartitionPeriod.Month"/>;
  /// use <see cref="PartitionPeriod.Day"/> for high-volume sites or short raw event retention.
  /// </summary>
  public PartitionPeriod RawEvents { get; set; } = PartitionPeriod.Month;

  /// <summary>
  /// Partition period of minute rollups (UTC). Default is <see cref="PartitionPeriod.Day"/>.
  /// </summary>
  public PartitionPeriod MinuteRollups { get; set; } = PartitionPeriod.Day;

  /// <summary>
  /// Partition period of hourly rollups (UTC). Default is <see cref="PartitionPeriod.Month"/>.
  /// </summary>
  public PartitionPeriod HourRollups { get; set; } = PartitionPeriod.Month;

  /// <summary>
  /// Partition period of daily rollups, in the reporting time zone. <see cref="PartitionPeriod.Month"/>
  /// or <see cref="PartitionPeriod.Year"/>. Default is <see cref="PartitionPeriod.Year"/>.
  /// </summary>
  public PartitionPeriod DayRollups { get; set; } = PartitionPeriod.Year;

  internal void Validate(RetentionOptions retention, bool storeRawEvents, Granularity granularities)
  {
    Validate(nameof(RawEvents), RawEvents, retention.RawEvents, storeRawEvents);
    Validate(nameof(MinuteRollups), MinuteRollups, retention.MinuteRollups, (granularities & Granularity.Minute) != 0);
    Validate(nameof(HourRollups), HourRollups, retention.HourRollups, (granularities & Granularity.Hour) != 0);
    Validate(nameof(DayRollups), DayRollups, retention.DayRollups, (granularities & Granularity.Day) != 0);
    if (DayRollups == PartitionPeriod.Day)
      throw new ArgumentException(
          "Storage.Partitioning.DayRollups must be Month or Year: a daily partition would hold a single daily bucket.");
  }

  static void Validate(string tier, PartitionPeriod period, TimeSpan? retention, bool enabled)
  {
    if (!Enum.IsDefined(period))
      throw new ArgumentException($"Storage.Partitioning.{tier} is not a valid partition period.");
    if (!enabled || retention == null || period == PartitionPeriod.Day)
      return;
    var shortest = period == PartitionPeriod.Month ? TimeSpan.FromDays(28) : TimeSpan.FromDays(365);
    if (retention.Value < shortest)
    {
      var unit = period.ToString().ToLowerInvariant();
      throw new ArgumentException(
          $"Retention.{tier} ({retention.Value.TotalDays:0.##} days) is shorter than one {unit} partition. " +
          $"Retention drops whole partitions, so data would be kept up to a {unit} longer than intended. " +
          $"Use a shorter Storage.Partitioning.{tier}.");
    }
  }
}

/// <summary>
/// What happens to bot traffic.
/// </summary>
public enum BotTraffic
{
  /// <summary>Bot events are discarded and counted in <see cref="AnalyticsStatistics.BotsExcluded"/>.</summary>
  Exclude,

  /// <summary>
  /// Bot events are recorded as <see cref="StandardEvents.Bot"/> events, separate from human
  /// traffic: they carry the page, hostname, original event name, bot name and bot category,
  /// and never a visitor or session, so human metrics are unaffected. Compare them with
  /// <see cref="Metric.BotHits"/>.
  /// </summary>
  TrackSeparately,
}

/// <summary>
/// Web enrichment options.
/// </summary>
public sealed class WebOptions
{
  /// <summary>
  /// Resolves IP addresses to locations. Default is null: no geolocation. The library ships
  /// no geolocation database; adapt one, or pass geography through event dimensions, for
  /// example from a CDN header.
  /// </summary>
  public IGeoLocator GeoLocator { get; set; }

  /// <summary>
  /// Parses user agents into browser, operating system and device.
  /// Default is <see cref="ZoneTree.Analytics.UserAgentParser.Default"/>.
  /// </summary>
  public IUserAgentParser UserAgentParser { get; set; } = ZoneTree.Analytics.UserAgentParser.Default;

  /// <summary>
  /// What happens to bot traffic. Default is <see cref="BotTraffic.Exclude"/>.
  /// </summary>
  public BotTraffic Bots { get; set; } = BotTraffic.Exclude;

  /// <summary>
  /// Bot detectors, in order; the first that recognizes a bot wins. Default is
  /// <see cref="BotDetector.UserAgent"/>. Add detectors (for example based on IP ranges) or
  /// clear the list to treat all traffic as human.
  /// </summary>
  public List<IBotDetector> BotDetectors { get; } = [BotDetector.UserAgent];

  /// <summary>
  /// Removes a trailing slash from page paths (except the root). Default is true.
  /// </summary>
  public bool TrimTrailingSlash { get; set; } = true;

  /// <summary>
  /// Lowercases page paths. Default is false.
  /// </summary>
  public bool LowercasePaths { get; set; }

  /// <summary>
  /// Additional referrer host to source name mappings, for example
  /// "news.ycombinator.com" to "Hacker News". Hosts match exactly or as a parent domain.
  /// </summary>
  public IDictionary<string, string> ReferrerSources { get; set; }

  /// <summary>
  /// Hosts treated as internal. Referrers from these hosts, or from the page host itself,
  /// count as direct traffic.
  /// </summary>
  public ISet<string> InternalHosts { get; set; }
}
