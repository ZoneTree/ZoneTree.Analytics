using System.Text.RegularExpressions;
using ZoneTree.Analytics;
using ZoneTree.Analytics.AspNetCore;

sealed class SettingsValidationException(string message, string key = null) : Exception(message)
{
  public string Key { get; } = key;
}

static partial class SettingsMapper
{
  // Dashboard visits recorded when Sites.TrackDashboardUsage is on.
  public const string DashboardUsageSite = "dashboard-usage";
  public static readonly string[] ReservedSites = [DashboardUsageSite, DashboardAnalyticsOptions.PerformanceSite, DashboardAnalyticsOptions.BenchmarkSite];

  [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
  private static partial Regex SiteIdPattern();

  [GeneratedRegex(@"^(\*\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*(:\d{1,5})?$")]
  private static partial Regex DomainPattern();

  public static string ResolveDataDirectory(DashboardSettings settings, string contentRoot) =>
      Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathRooted(settings.General.DataDirectory)
          ? settings.General.DataDirectory
          : Path.Combine(contentRoot, settings.General.DataDirectory)));

  // Checks the dashboard's own rules, then the engine's.
  public static void Validate(DashboardSettings settings, string contentRoot, string systemDirectory)
  {
    if (settings == null) throw new SettingsValidationException("Settings are required.");
    foreach (var section in typeof(DashboardSettings).GetProperties())
      if (section.GetValue(settings) == null)
        throw new SettingsValidationException($"The {SettingsSchema.Camel(section.Name)} section is missing.", SettingsSchema.Camel(section.Name));
    ValidateNumbers(settings);
    ValidateGeneral(settings, contentRoot, systemDirectory);
    ValidateSites(settings.Sites);
    if (settings.Rollups.Granularities.Count == 0)
      throw new SettingsValidationException("Choose at least one rollup granularity.", "rollups.granularities");
    if (settings.Indexes.Dimensions.Count > 0 && !settings.Storage.StoreRawEvents)
      throw new SettingsValidationException("Dimension indexes copy raw events. Store raw events, or remove the dimension indexes.", "indexes.dimensions");
    try
    {
      ToOptions(settings, contentRoot, null).Validate();
    }
    catch (ArgumentException error)
    {
      throw new SettingsValidationException(error.Message);
    }
  }

  // Numbers stay inside their [Setting] ranges; an enum holds one of its names.
  static void ValidateNumbers(DashboardSettings settings)
  {
    foreach (var section in SettingsSchema.Document.Sections)
      foreach (var field in section.Fields)
        Check(field, settings);

    static void Check(SettingField field, DashboardSettings settings)
    {
      if (field.Fields != null)
      {
        foreach (var child in field.Fields) Check(child, settings);
        return;
      }
      var value = SettingsDocument.Read(settings, field.Key);
      if (value == null)
      {
        if (!field.Nullable && field.Type is "number" or "select" or "toggle")
          throw new SettingsValidationException($"{field.Label} is required.", field.Key);
        return;
      }
      if (field.Type == "number" && Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) is var number
          && (double.IsNaN(number) || number < (field.Min ?? double.MinValue) || number > (field.Max ?? double.MaxValue)))
        throw new SettingsValidationException(
            $"{field.Label} must be between {field.Min:0.###} and {field.Max:0.###}{(field.Unit == null ? "" : " " + field.Unit)}.", field.Key);
      if (value is Enum && !Enum.IsDefined(value.GetType(), value))
        throw new SettingsValidationException($"{field.Label} is not a valid choice.", field.Key);
      if (field.Type == "select" && field.Options != null && !field.Options.Any(option => option.Value.ToString() == value.ToString()))
        throw new SettingsValidationException($"{field.Label} is not a valid choice.", field.Key);
    }
  }

  static void ValidateGeneral(DashboardSettings settings, string contentRoot, string systemDirectory)
  {
    var general = settings.General;
    if (string.IsNullOrWhiteSpace(general.TimeZone) || !IsIanaTimeZone(general.TimeZone))
      throw new SettingsValidationException("Choose an IANA time zone, such as Europe/Berlin or UTC.", "general.timeZone");
    if (string.IsNullOrWhiteSpace(general.DataDirectory))
      throw new SettingsValidationException("Choose a data directory.", "general.dataDirectory");
    var data = ResolveDataDirectory(settings, contentRoot);
    if (systemDirectory != null && Overlaps(data, systemDirectory))
      throw new SettingsValidationException("The analytics data directory must be separate from the dashboard's system directory.", "general.dataDirectory");
    if (settings.Storage.WriteAheadLogDirectory is { } wal && string.IsNullOrWhiteSpace(wal))
      settings.Storage.WriteAheadLogDirectory = null;
  }

  public static bool IsIanaTimeZone(string id)
  {
    if (id == "UTC") return true;
    try
    {
      TimeZoneInfo.FindSystemTimeZoneById(id);
    }
    catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
    {
      return false;
    }
    return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _);
  }

  static bool Overlaps(string first, string second)
  {
    second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
    return string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
        || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
  }

  static void ValidateSites(SitesSettings sites)
  {
    if (sites.Items.Count == 0)
      throw new SettingsValidationException("Add at least one site.", "sites.items");
    var ids = new HashSet<string>(StringComparer.Ordinal);
    var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var site in sites.Items)
    {
      if (site == null) throw new SettingsValidationException("A site is empty.", "sites.items");
      site.Id = site.Id?.Trim();
      site.Name = site.Name?.Trim();
      if (string.IsNullOrEmpty(site.Id) || !SiteIdPattern().IsMatch(site.Id))
        throw new SettingsValidationException(
            $"Site ID '{site.Id}' must be 1 to 64 lowercase letters, digits, dots, dashes or underscores.", "sites.items");
      if (ReservedSites.Contains(site.Id))
        throw new SettingsValidationException($"The site ID '{site.Id}' is reserved by the dashboard.", "sites.items");
      if (!ids.Add(site.Id))
        throw new SettingsValidationException($"Two sites use the ID '{site.Id}'.", "sites.items");
      if (string.IsNullOrEmpty(site.Name) || site.Name.Length > 100)
        throw new SettingsValidationException($"Give site '{site.Id}' a name of at most 100 characters.", "sites.items");
      site.Domains = [.. (site.Domains ?? []).Select(NormalizeDomain).Where(domain => domain.Length > 0).Distinct(StringComparer.Ordinal)];
      foreach (var domain in site.Domains)
      {
        if (!DomainPattern().IsMatch(domain))
          throw new SettingsValidationException($"'{domain}' is not a host name. Use example.com, or *.example.com for subdomains.", "sites.items");
        if (!domains.TryAdd(domain, site.Id))
          throw new SettingsValidationException($"The domain '{domain}' belongs to two sites.", "sites.items");
      }
    }
    if (string.IsNullOrEmpty(sites.PrimarySite)) sites.PrimarySite = sites.Items[0].Id;
    if (!ids.Contains(sites.PrimarySite))
      throw new SettingsValidationException("The primary site must be one of the sites.", "sites.primarySite");
  }

  // Accepts a pasted URL: https://www.example.com/path → www.example.com.
  static string NormalizeDomain(string value)
  {
    value = (value ?? "").Trim().ToLowerInvariant();
    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
      value = uri.IsDefaultPort ? uri.Host : uri.Authority;
    return value.TrimEnd('/', '.');
  }

  public static AnalyticsOptions ToOptions(DashboardSettings settings, string contentRoot, ILogger logger)
  {
    var options = new AnalyticsOptions
    {
      DataDirectory = ResolveDataDirectory(settings, contentRoot),
      DefaultSite = settings.Sites.PrimarySite,
      TimeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.General.TimeZone),
      FirstDayOfWeek = settings.General.FirstDayOfWeek,
    };
    if (logger != null) options.UseLogger(logger);

    var tracking = settings.Tracking;
    options.Web.Bots = tracking.Bots;
    options.Web.TrimTrailingSlash = tracking.TrimTrailingSlash;
    options.Web.LowercasePaths = tracking.LowercasePaths;
    foreach (var host in tracking.InternalHosts) options.Web.InternalHosts.Add(host);
    foreach (var (host, source) in tracking.ReferrerSources) options.Web.ReferrerSources[host] = source;

    var privacy = settings.Privacy;
    options.Privacy.VisitorIdentification = privacy.VisitorIdentification;
    options.Privacy.GeoPrecision = privacy.GeoPrecision;
    options.Privacy.CollectUserAgentDetails = privacy.CollectUserAgentDetails;
    options.Privacy.CollectVersions = privacy.CollectVersions;
    options.Privacy.IncludeQueryString = privacy.IncludeQueryString;
    options.Privacy.AllowedDimensions = privacy.RestrictDimensions ? new HashSet<string>(privacy.AllowedDimensions, StringComparer.Ordinal) : null;
    foreach (var dimension in privacy.DeniedDimensions) options.Privacy.DeniedDimensions.Add(dimension);
    options.Privacy.MaxCustomDimensions = privacy.MaxCustomDimensions;
    options.Privacy.MaxCustomValues = privacy.MaxCustomValues;
    options.Privacy.MaxValueLength = privacy.MaxValueLength;

    var sessions = settings.Sessions;
    options.Sessions.Enabled = sessions.Enabled;
    options.Sessions.Timeout = TimeSpan.FromMinutes(sessions.TimeoutMinutes);
    options.Sessions.MaxCachedSessions = sessions.MaxCachedSessions;
    options.Sessions.Dimensions.AddRange(sessions.Dimensions);

    var retention = settings.Retention;
    options.Retention.RawEvents = Days(retention.RawEventsDays);
    options.Retention.MinuteRollups = Days(retention.MinuteRollupsDays);
    options.Retention.HourRollups = Days(retention.HourRollupsDays);
    options.Retention.DayRollups = Days(retention.DayRollupsDays);
    options.Retention.CheckInterval = retention.CheckIntervalMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null;

    var rollups = settings.Rollups;
    options.Rollups.Granularities = rollups.Granularities.Aggregate(default(Granularity), (all, granularity) => all | granularity switch
    {
      RollupGranularity.Minute => Granularity.Minute,
      RollupGranularity.Hour => Granularity.Hour,
      _ => Granularity.Day,
    });
    options.Rollups.Definitions.Clear();
    foreach (var definition in rollups.Definitions)
      options.Rollups.Definitions.Add(new RollupDefinition([.. definition?.Dimensions ?? []]) { Events = definition?.Events?.ToArray() });
    foreach (var value in rollups.ValueAggregates)
    {
      var aggregates = (value.Percentiles ? ValueAggregates.Percentiles : ValueAggregates.None)
          | (value.FirstAndLast ? ValueAggregates.FirstAndLast : ValueAggregates.None);
      options.Rollups.ValueAggregates[value.Value ?? ""] = aggregates;
    }
    options.Rollups.DistinctDimensions.AddRange(rollups.DistinctDimensions);
    options.Rollups.HyperLogLogPrecision = rollups.HyperLogLogPrecision;

    var indexes = settings.Indexes;
    options.Storage.DimensionIndexes.Dimensions.AddRange(indexes.Dimensions);
    options.Storage.DimensionIndexes.RecordsPerPartition = indexes.DimensionRecordsPerPartition;
    foreach (var index in indexes.Rollups)
      options.Storage.RollupIndexes.Definitions.Add(new RollupIndexDefinition(index.Dimension, [.. index.GroupDimensions ?? []]));
    options.Storage.RollupIndexes.RecordsPerPartition = indexes.RollupRecordsPerPartition;

    var ingestion = settings.Ingestion;
    options.Ingestion.WorkerCount = ingestion.WorkerCount;
    options.Ingestion.RollupWriterCount = ingestion.RollupWriterCount;
    options.Ingestion.QueueCapacity = ingestion.QueueCapacity;
    options.Ingestion.QueueFullBehavior = ingestion.QueueFullBehavior;
    options.Ingestion.FlushInterval = TimeSpan.FromSeconds(ingestion.FlushIntervalSeconds);
    options.Ingestion.MaxBufferedRollupRows = ingestion.MaxBufferedRollupRows;
    options.Ingestion.MaxFutureSkew = TimeSpan.FromMinutes(ingestion.MaxFutureSkewMinutes);

    var queries = settings.Queries;
    options.Queries.Timeout = queries.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
    options.Queries.MaxDegreeOfParallelism = queries.MaxDegreeOfParallelism > 0 ? queries.MaxDegreeOfParallelism : Environment.ProcessorCount;
    options.Queries.MaxGroupBy = queries.MaxGroupBy;
    options.Queries.MaxGroups = queries.MaxGroups;
    options.Queries.MaxTimeBuckets = queries.MaxTimeBuckets;
    options.Queries.MaxFilledRows = queries.MaxFilledRows;

    var storage = settings.Storage;
    options.Storage.StoreRawEvents = storage.StoreRawEvents;
    options.Storage.WriteAheadLogDirectory = string.IsNullOrWhiteSpace(storage.WriteAheadLogDirectory) ? null
        : Path.IsPathRooted(storage.WriteAheadLogDirectory) ? storage.WriteAheadLogDirectory
        : Path.GetFullPath(Path.Combine(contentRoot, storage.WriteAheadLogDirectory));
    options.Storage.PartitionIdleTimeout = TimeSpan.FromMinutes(storage.PartitionIdleTimeoutMinutes);
    options.Storage.Partitioning.RawEvents = storage.RawEventsPerPartition;
    options.Storage.Partitioning.MinuteRollups = storage.MinuteRollupsPerPartition;
    options.Storage.Partitioning.HourRollups = storage.HourRollupsPerPartition;
    options.Storage.Partitioning.DayRollups = storage.DayRollupsPerPartition;
    Apply(storage.RawEventStore, options.Storage.RawEvents);
    Apply(storage.RollupStore, options.Storage.Rollups);
    Apply(storage.SessionStore, options.Storage.Sessions);
    Apply(storage.DimensionIndexStore, options.Storage.DimensionIndexes.Store);
    Apply(storage.RollupIndexStore, options.Storage.RollupIndexes.Store);
    return options;
  }

  static TimeSpan? Days(int? days) => days is { } value ? TimeSpan.FromDays(value) : null;

  static void Apply(StoreSettings settings, StoreOptions store)
  {
    store.WriteAheadLogMode = settings.WriteAheadLogMode;
    store.MutableSegmentMaxItemCount = settings.MutableSegmentMaxItemCount;
    store.DiskSegmentMaxItemCount = settings.DiskSegmentMaxItemCount;
    store.EnableSingleSegmentGarbageCollection = settings.EnableSingleSegmentGarbageCollection;
  }

  // The settings that ASP.NET Core reads on every request apply without reopening the engine.
  public static void ApplyLive(DashboardSettings settings, AnalyticsWebOptions web)
  {
    web.AllowedClientEvents = settings.Tracking.AllowedClientEvents.Count == 0 ? null
        : new HashSet<string>(settings.Tracking.AllowedClientEvents, StringComparer.Ordinal);
    web.MaxPayloadBytes = settings.Tracking.MaxPayloadBytes;
    web.MaxReportingPayloadBytes = settings.Reporting.MaxRequestBytes;
    web.MaxReportingResponseBytes = settings.Reporting.MaxResponseBytes;
  }
}
