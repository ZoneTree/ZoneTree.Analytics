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
    Queries ??= new();
    Ingestion.Validate();
    Rollups.Validate();
    Retention.Validate();
    Privacy.Validate();
    Sessions.Validate(Privacy);
    Storage.Validate(Retention, Rollups.Granularities);
    Queries.Validate();
  }
}
