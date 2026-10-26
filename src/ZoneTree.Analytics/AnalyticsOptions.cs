using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics;

/// <summary>
/// Configuration of an <see cref="AnalyticsEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// The engine copies the options when it is created, so they are fixed for its lifetime:
/// changing them afterwards has no effect. Create a new engine to apply other options.
/// </para>
/// <para>
/// Option groups and collections are never null: set their members in place, for example
/// <c>new AnalyticsOptions { Retention = { RawEvents = TimeSpan.FromDays(30) } }</c> or
/// <c>options.Rollups.Definitions.Add(...)</c>.
/// </para>
/// </remarks>
public sealed class AnalyticsOptions
{
  /// <summary>
  /// The root directory of all analytics data. Default is "data/analytics".
  /// </summary>
  public string DataDirectory { get; set; } = "data/analytics";

  /// <summary>
  /// The site used for events and queries that do not specify one. Default is "default".
  /// Must contain valid Unicode, no control characters and no surrounding whitespace, and be
  /// at most 128 characters: the form in which events store their site.
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
  public List<IEventFilter> EventFilters { get; private set; } = [];

  /// <summary>
  /// Enrichers that add, change or remove dimensions after the built-in enrichment, in order.
  /// Create one from a lambda with <see cref="EventEnricher.Create"/> or use
  /// <see cref="EventEnricher.NormalizePage"/>.
  /// </summary>
  public List<IEventEnricher> Enrichers { get; private set; } = [];

  /// <summary>
  /// Ingestion pipeline options.
  /// </summary>
  public IngestionOptions Ingestion { get; private set; } = new();

  /// <summary>
  /// Session tracking options.
  /// </summary>
  public SessionOptions Sessions { get; private set; } = new();

  /// <summary>
  /// Pre-aggregation options.
  /// </summary>
  public RollupOptions Rollups { get; private set; } = new();

  /// <summary>
  /// Data retention options.
  /// </summary>
  public RetentionOptions Retention { get; private set; } = new();

  /// <summary>
  /// Privacy options.
  /// </summary>
  public PrivacyOptions Privacy { get; private set; } = new();

  /// <summary>
  /// Web enrichment options.
  /// </summary>
  public WebOptions Web { get; private set; } = new();

  /// <summary>
  /// Storage options.
  /// </summary>
  public StorageOptions Storage { get; private set; } = new();

  /// <summary>
  /// Query options.
  /// </summary>
  public QueryOptions Queries { get; private set; } = new();

  /// <summary>
  /// A deep copy of the configuration: option groups and collections are copied, while
  /// extensions, hooks, the logger and time zones are shared.
  /// </summary>
  internal AnalyticsOptions Clone()
  {
    var clone = (AnalyticsOptions)MemberwiseClone();
    clone.EventFilters = [.. EventFilters];
    clone.Enrichers = [.. Enrichers];
    clone.Ingestion = Ingestion.Clone();
    clone.Sessions = Sessions.Clone();
    clone.Rollups = Rollups.Clone();
    clone.Retention = Retention.Clone();
    clone.Privacy = Privacy.Clone();
    clone.Web = Web.Clone();
    clone.Storage = Storage.Clone();
    clone.Queries = Queries.Clone();
    return clone;
  }

  /// <summary>
  /// Checks the options the way the engine does before it opens the data directory, without
  /// touching storage. Compatibility with an existing data directory (time zone, sketch
  /// precision, write-ahead-log directory) and ZoneTree store settings are checked only when an
  /// engine opens.
  /// </summary>
  /// <exception cref="ArgumentException">The options are invalid.</exception>
  public void Validate()
  {
    if (string.IsNullOrWhiteSpace(DataDirectory))
      throw new ArgumentException("DataDirectory is required.");
    Text.ValidateStoredName(DefaultSite, nameof(DefaultSite));
    // Events name their site the same way, so the default site has one identity.
    if (Text.Name(DefaultSite) != DefaultSite)
      throw new ArgumentException(
          $"DefaultSite must be a site name as events store it: no surrounding whitespace and at most {Text.MaxNameLength} characters.");
    if (TimeZone == null)
      throw new ArgumentException("TimeZone is required.");
    Ingestion.Validate();
    Rollups.Validate();
    Retention.Validate();
    Privacy.Validate();
    Web.Validate();
    Sessions.Validate(Privacy);
    Storage.Validate();
    Queries.Validate();
  }
}
