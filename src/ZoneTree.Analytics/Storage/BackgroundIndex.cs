using ZoneTree.Analytics.Internal;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The lifecycle of optional index copies of authoritative data, which background index
/// maintenance builds and repairs while the engine ingests and answers queries.
/// </summary>
/// <remarks>
/// <para>
/// Opening decides whether the copies can be reused, need a repair of the days whose writes may
/// have been interrupted, or need a rebuild. Only an interrupted build of the same configuration
/// resumes from its checkpoint, and then repairs the days written since it began; any other
/// reason drops the copies, before ingestion writes new ones, and builds them again.
/// </para>
/// <para>
/// A completed build, and an orderly shutdown, record the coverage: format, configuration,
/// partitions and a clean checkpoint. Once a copy fails, the copies after it are skipped: no
/// checkpoint or coverage may claim them, so the build stops until the engine opens again.
/// </para>
/// </remarks>
abstract class BackgroundIndex : IDisposable
{
  protected const string UncleanReason = "missing or unclean checkpoint";

  readonly string stage;
  readonly string cleanKey;
  readonly string configurationKey;
  readonly string checkpointPrefix;
  readonly string formatKey;
  readonly int format;
  readonly string formatDescription;
  string manifest;
  Dictionary<IndexRecovery, long[]> dirtyDays = [];
  int invalid;
  long revision;
  volatile bool ready;

  /// <param name="meta">The engine metadata.</param>
  /// <param name="logger">Receives the stage logs.</param>
  /// <param name="root">The directory of the index stores.</param>
  /// <param name="stage">The name that starts the stage logs, such as "Dimension index".</param>
  /// <param name="keyPrefix">The prefix of the clean and checkpoint metadata keys.</param>
  /// <param name="configurationKey">The metadata key of the configuration manifest.</param>
  /// <param name="formatKey">The metadata key of the index format.</param>
  /// <param name="format">The current index format.</param>
  /// <param name="formatDescription">What the format describes, for its errors.</param>
  protected BackgroundIndex(MetaStore meta, ILogger logger, string root, string stage, string keyPrefix,
      string configurationKey, string formatKey, int format, string formatDescription)
  {
    Meta = meta;
    Logger = logger;
    Root = root;
    this.stage = stage;
    cleanKey = keyPrefix + ".clean";
    checkpointPrefix = keyPrefix + ".backfill";
    this.configurationKey = configurationKey;
    this.formatKey = formatKey;
    this.format = format;
    this.formatDescription = formatDescription;
  }

  protected MetaStore Meta { get; }

  protected ILogger Logger { get; }

  /// <summary>The directory of the index stores.</summary>
  protected string Root { get; }

  /// <summary>The stores and other resources the index owns.</summary>
  protected List<IDisposable> Resources { get; } = [];

  /// <summary>Whether opening decided to rebuild the copies.</summary>
  protected bool Rebuilding { get; private set; }

  /// <summary>The days to repair, by intent journal; empty when there is no repair.</summary>
  protected IReadOnlyDictionary<IndexRecovery, long[]> RepairDays { get; private set; } = new Dictionary<IndexRecovery, long[]>();

  /// <summary>Whether a build or a repair has completed, or none was needed.</summary>
  protected bool IsReady => ready;

  /// <summary>Changes whenever queries must not keep reading the copies they selected.</summary>
  protected long Revision => Interlocked.Read(ref revision);

  public bool IsHealthy => Volatile.Read(ref invalid) == 0;

  /// <summary>Whether the copies are being built or repaired; queries read the authoritative data meanwhile.</summary>
  public bool IsBuilding => IndexCount > 0 && !ready && IsHealthy;

  /// <summary>The number of configured logical indexes.</summary>
  protected abstract int IndexCount { get; }

  /// <summary>The number of physical index stores.</summary>
  protected abstract int StoreCount { get; }

  /// <summary>Why the index is disabled, for the logs.</summary>
  protected abstract string DisabledReason { get; }

  /// <summary>Whether both the source and the copies have a write-ahead log.</summary>
  protected abstract bool Durable { get; }

  /// <summary>The intents of the days whose writes may have been interrupted.</summary>
  protected abstract IEnumerable<IndexRecovery> Journals { get; }

  /// <summary>Whether there is no source data to copy, so a new build completes at once.</summary>
  protected abstract bool NothingToCopy { get; }

  /// <summary>
  /// Opens the stores of the configured indexes and drops those of removed ones. Adds the
  /// reasons to rebuild that the stores show, and returns their number of partitions.
  /// </summary>
  protected abstract int OpenStores(List<string> reasons);

  /// <summary>Drops every copy, before a new build.</summary>
  protected abstract void DropCopies();

  /// <summary>Records the partitions of the stores in the coverage.</summary>
  protected abstract void SavePartitions(Dictionary<string, string> entries);

  /// <summary>The failure of a build that cannot succeed until the engine opens again.</summary>
  protected abstract AnalyticsStorageException BuildFailed(Exception cause);

  /// <summary>Runs the build or repair that opening decided, on the background maintenance thread.</summary>
  public abstract void Build(CancellationToken cancellationToken);

  /// <summary>Whether an interrupted build may resume despite a reason to rebuild.</summary>
  protected virtual bool Resumes(string reason) => reason == UncleanReason;

  /// <summary>Runs when a build completes, before its coverage is recorded.</summary>
  protected virtual void Completing()
  {
  }

  /// <summary>Marks the copies unusable until the engine opens again.</summary>
  public void Invalidate()
  {
    Volatile.Write(ref invalid, 1);
    Interlocked.Increment(ref revision);
  }

  /// <summary>Makes queries that selected the copies stop reading them.</summary>
  protected void ChangeRevision() => Interlocked.Increment(ref revision);

  /// <summary>
  /// Opens the index and decides its work, which <see cref="Build"/> then runs in the
  /// background. A new build drops the old copies first.
  /// </summary>
  /// <param name="manifest">The configuration of the indexes.</param>
  /// <param name="reasons">Reasons to rebuild that only the store knows.</param>
  protected void Open(string manifest, IEnumerable<string> reasons)
  {
    this.manifest = manifest;
    using var startup = new StageLog(Logger, stage + " initialization", Root);
    using var check = new StageLog(Logger, stage + " backfill check", Root);
    var stored = Meta.Get(formatKey);
    if (stored != null)
      StorageFormats.Require(stored, format, formatDescription);
    var found = new List<string>();
    // Indexes that were never initialized have no checkpoint, manifest or format to compare.
    if (stored == null) found.Add("new indexes");
    else
    {
      if (Meta.Get(cleanKey) != "true") found.Add(UncleanReason);
      if (Meta.Get(configurationKey) != manifest) found.Add("configuration changed");
    }
    if (!Durable) found.Add("write-ahead logging disabled");
    found.AddRange(reasons);
    var partitions = OpenStores(found);
    Rebuilding = found.Count > 0 && IndexCount > 0;
    var checkpoint = Checkpoint();
    // Only an interrupted build of the same configuration resumes; any other reason starts over.
    var resuming = Rebuilding && checkpoint.Copying && found.All(Resumes);
    dirtyDays = Journals.ToDictionary(journal => journal, journal => journal.Days);
    // A new build copies every day; one that resumes repairs the days written since it began.
    // Without indexes there is nothing to repair: their intents are cleared.
    RepairDays = IndexCount == 0 || (Rebuilding && !resuming)
        ? new Dictionary<IndexRecovery, long[]>() : dirtyDays.Where(pair => pair.Value.Length > 0).ToDictionary();
    var dirty = dirtyDays.Values.Sum(days => days.Length);
    var decision = IndexCount == 0 ? "disabled" : Rebuilding ? "rebuild required" : dirty > 0 ? "repair affected days" : "reuse existing indexes";
    var reason = IndexCount == 0 ? DisabledReason : Rebuilding ? string.Join(", ", found) : dirty > 0 ? "durable write intents require reconciliation" : "clean checkpoint and matching manifests";
    check.Complete($"decision={decision}; indexes={IndexCount}; physical stores={StoreCount}; partitions={partitions}; dirty days={dirty}; resuming={resuming}; reasons={reason}");
    if (Rebuilding && !resuming)
    {
      Meta.Set(cleanKey, "false");
      checkpoint.Begin();
      DropCopies();
      // An interrupted build of the new copies resumes at the next open, while they stay unclean.
      Meta.Update(entries =>
      {
        SaveCoverage(entries);
        entries[cleanKey] = "false";
      });
      checkpoint.Cleared();
      if (NothingToCopy)
        Build(CancellationToken.None);
    }
    if (!Rebuilding && RepairDays.Count == 0)
    {
      ready = true;
      MarkClean();
    }
    startup.Complete($"decision={(ready ? IndexCount == 0 ? "disabled" : Rebuilding ? "rebuild completed" : "reuse existing indexes" : Rebuilding ? "rebuild in background" : "repair in background")}; indexes={IndexCount}; physical stores={StoreCount}");
  }

  protected IndexBackfillCheckpoint Checkpoint() => new(Meta, checkpointPrefix, manifest);

  /// <summary>Stops a build once a copy has failed.</summary>
  protected void RequireHealthy()
  {
    if (!IsHealthy)
      throw BuildFailed(null);
  }

  /// <summary>Records the coverage of a completed build, and makes the copies available to queries.</summary>
  protected void Complete(IndexBackfillCheckpoint checkpoint)
  {
    RequireHealthy();
    Completing();
    Meta.Update(entries =>
    {
      SaveCoverage(entries);
      checkpoint.Clear(entries);
    });
    foreach (var (journal, days) in dirtyDays)
      journal.ClearRepaired(days);
    ready = true;
    Interlocked.Increment(ref revision);
  }

  /// <summary>
  /// Records the coverage at an orderly shutdown, when the copies are complete: no day needs a
  /// repair then. An unfinished build keeps its checkpoint and resumes at the next open.
  /// </summary>
  public void MarkClean()
  {
    if (!ready || !IsHealthy) return;
    Meta.Update(entries =>
    {
      SaveCoverage(entries);
      foreach (var journal in Journals) journal.Clear(entries);
      Checkpoint().Clear(entries);
    });
  }

  void SaveCoverage(Dictionary<string, string> entries)
  {
    entries[cleanKey] = Durable ? "true" : "false";
    entries[formatKey] = StorageFormats.Text(format);
    entries[configurationKey] = manifest;
    SavePartitions(entries);
  }

  public void Dispose() => Disposal.DisposeAll(Resources);
}
