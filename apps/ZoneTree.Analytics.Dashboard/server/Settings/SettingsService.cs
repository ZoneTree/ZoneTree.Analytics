using System.Text.Json;
using ZoneTree.Analytics.AspNetCore;

sealed record SettingsReview(IReadOnlyList<SettingChange> Changes, IReadOnlyList<string> Warnings);

sealed record SettingsSaveResult(DashboardSettings Settings, IReadOnlyList<SettingChange> Changes, bool Reopened, bool RestartRequired);

sealed class SettingsConfirmationRequiredException(IReadOnlyList<string> warnings)
    : Exception("Confirm the changes that delete or stop keeping data.")
{
  public IReadOnlyList<string> Warnings { get; } = warnings;
}

// The stored settings are the source of truth once setup is complete. Saving validates them,
// reopens the engine when an engine option changed, then stores them and applies the rest.
sealed class SettingsService
{
  const string Key = "settings";
  static readonly SecuritySettings DefaultSecurity = new();

  readonly SystemStore store;
  readonly EngineHost engines;
  readonly AnalyticsWebOptions web;
  readonly UserStore users;
  readonly AuditLog audit;
  readonly string contentRoot;
  readonly int startupConcurrentRequests;
  readonly SemaphoreSlim saving = new(1, 1);
  volatile DashboardSettings current;

  public SettingsService(SystemStore store, EngineHost engines, AnalyticsWebOptions web, UserStore users, AuditLog audit,
      IWebHostEnvironment environment)
  {
    this.store = store;
    this.engines = engines;
    this.web = web;
    this.users = users;
    this.audit = audit;
    contentRoot = environment.ContentRootPath;
    current = store.Get<DashboardSettings>(Key);
    startupConcurrentRequests = web.MaxConcurrentReportingRequests;
    if (current != null) SettingsMapper.ApplyLive(current, web);
  }

  public bool IsConfigured => current != null;

  // A snapshot: callers must not modify it. Use Copy() for an editable document.
  public DashboardSettings Current => current;

  public DashboardSettings Copy() => current == null ? null : SettingsDocument.Clone(current);

  public SecuritySettings Security => current?.Security ?? DefaultSecurity;

  // The time zone chosen during setup, or UTC before it.
  public TimeZoneInfo ReportingTimeZone => TimeZoneInfo.FindSystemTimeZoneById(current?.General.TimeZone ?? "UTC");

  public bool RestartRequired => current != null && current.Reporting.MaxConcurrentRequests != startupConcurrentRequests;

  public string ContentRoot => contentRoot;

  // The reporting limiter reads its capacity once, when the host starts.
  public static int StoredConcurrentRequests(SystemStore store) =>
      store.Get<DashboardSettings>(Key)?.Reporting.MaxConcurrentRequests ?? new ReportingSettings().MaxConcurrentRequests;

  public static DashboardSettings CreateDefaults()
  {
    var settings = new DashboardSettings();
    settings.Sites.Items.Add(new SiteSettings { Id = "main", Name = "My website" });
    settings.Sites.PrimarySite = "main";
    return settings;
  }

  public void Validate(DashboardSettings candidate) => SettingsMapper.Validate(candidate, contentRoot, store.Directory);

  public SettingsReview Review(DashboardSettings candidate)
  {
    Validate(candidate);
    if (current == null) return new([], []);
    var changes = SettingsDocument.Diff(current, candidate);
    var fixedChange = changes.FirstOrDefault(change => change.Tier == SettingTier.Fixed);
    if (fixedChange != null)
      throw new SettingsValidationException($"{fixedChange.Label} is stored with the data and cannot change after setup.", fixedChange.Key);
    return new(changes, DataLossWarnings(current, candidate));
  }

  public async Task<SettingsSaveResult> SaveAsync(DashboardSettings candidate, string actor, bool confirmDataLoss, HttpContext context)
  {
    await saving.WaitAsync().ConfigureAwait(false);
    try
    {
      if (current == null) throw new InvalidOperationException("Finish the setup wizard first.");
      var review = Review(candidate);
      if (review.Changes.Count == 0) return new(current, [], false, RestartRequired);
      if (review.Warnings.Count > 0 && !confirmDataLoss) throw new SettingsConfirmationRequiredException(review.Warnings);
      var previous = current;
      var reopen = review.Changes.Any(change => change.Tier == SettingTier.Reopen);
      if (reopen) await engines.ReopenAsync(candidate, previous).ConfigureAwait(false);
      store.Put(Key, candidate);
      current = candidate;
      SettingsMapper.ApplyLive(candidate, web);
      users.RemoveSites(candidate.Sites.Items.Select(site => site.Id).ToHashSet(StringComparer.Ordinal));
      audit.Write(actor, "settings.updated", string.Join(", ", review.Changes.Select(change => change.Label).Distinct()), context);
      return new(candidate, review.Changes, reopen, RestartRequired);
    }
    finally { saving.Release(); }
  }

  // Opens the engine with the setup wizard's settings; they are stored only when it opens. The
  // caller allows it only while no administrator exists.
  public async Task CompleteSetupAsync(DashboardSettings settings)
  {
    await saving.WaitAsync().ConfigureAwait(false);
    try
    {
      Validate(settings);
      await engines.ReopenAsync(settings, current).ConfigureAwait(false);
      store.Put(Key, settings);
      current = settings;
      SettingsMapper.ApplyLive(settings, web);
    }
    finally { saving.Release(); }
  }

  static List<string> DataLossWarnings(DashboardSettings before, DashboardSettings after)
  {
    var warnings = new List<string>();
    Retention("Raw events", before.Retention.RawEventsDays, after.Retention.RawEventsDays);
    Retention("Minute rollups", before.Retention.MinuteRollupsDays, after.Retention.MinuteRollupsDays);
    Retention("Hourly rollups", before.Retention.HourRollupsDays, after.Retention.HourRollupsDays);
    Retention("Daily rollups", before.Retention.DayRollupsDays, after.Retention.DayRollupsDays);
    if (before.Storage.StoreRawEvents && !after.Storage.StoreRawEvents)
      warnings.Add("New raw events will not be stored: ad-hoc queries, real-time counts and erasure requests cover only earlier events.");
    var removedGranularities = before.Rollups.Granularities.Except(after.Rollups.Granularities).ToList();
    if (removedGranularities.Count > 0)
      warnings.Add($"{string.Join(", ", removedGranularities)} rollups stop being kept; their reports are answered from raw events while they last.");
    var kept = after.Rollups.Definitions.Select(Id).ToHashSet(StringComparer.Ordinal);
    var removed = before.Rollups.Definitions.Select(Id).Where(id => !kept.Contains(id)).ToList();
    if (removed.Count > 0)
      warnings.Add($"Rollups stop being kept for {string.Join("; ", removed.Select(id => id.Length == 0 ? "totals" : id))}. " +
          "Their reports are answered from raw events, so periods without raw events lose them.");
    return warnings;

    void Retention(string label, int? previous, int? next)
    {
      if (next is { } days && (previous == null || days < previous))
        warnings.Add($"{label} older than {days} days will be deleted.");
    }

    static string Id(RollupDefinitionSettings definition) =>
        string.Join(",", (definition.Dimensions ?? []).Order(StringComparer.Ordinal)) +
        (definition.Events == null ? "" : " (" + string.Join(",", definition.Events.Order(StringComparer.Ordinal)) + ")");
  }

  public static JsonElement ToJson(DashboardSettings settings) => JsonSerializer.SerializeToElement(settings, SystemStore.Json);
}
