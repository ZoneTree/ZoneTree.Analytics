using System.Globalization;
using System.Threading.Channels;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Privacy;
using ZoneTree.Analytics.Query;
using ZoneTree.Analytics.Storage;
using ZoneTree.Analytics.Web;
using ZoneTree.Logger;

namespace ZoneTree.Analytics;

/// <summary>
/// An embedded analytics engine backed by ZoneTree.
/// </summary>
/// <remarks>
/// <para>
/// Create one engine per data directory and keep it for the application lifetime; a second engine
/// on the same directory, in this or another process, fails with an <see cref="IOException"/>.
/// All members are thread-safe. <see cref="Track"/> never blocks by default: events are
/// queued and processed by background workers.
/// </para>
/// <para>
/// Storage layout under <see cref="AnalyticsOptions.DataDirectory"/>. Each data tier is a
/// sequence of partitions named after their start day; a tier starts a new partition when the
/// latest one holds the records configured in <see cref="PartitioningOptions"/> (10 million by
/// default):
/// <code>
/// engine.lock                 held exclusively while the engine is open
/// meta/                       configuration, secrets, salts (two checksummed files)
/// sessions/                   active sessions
/// events/2026-10-11/          raw events
/// rollups/minute/2026-10-11/  minute rollups
/// rollups/hour/2026-10-11/    hourly rollups
/// rollups/day/2026-10-11/     daily rollups
/// </code>
/// </para>
/// </remarks>
public sealed class AnalyticsEngine : IAnalyticsTracker, IDisposable, IAsyncDisposable
{
  const string FormatVersion = "1";

  readonly AnalyticsOptions options;

  readonly ILogger logger;

  readonly string dataRoot;

  readonly DirectoryLock directoryLock;

  readonly MetaStore meta;

  readonly IZoneTree<ulong, SessionState> sessions;

  readonly IMaintainer sessionsMaintainer;

  readonly RollupStore rollups;

  readonly RawEventStore rawEvents;

  readonly DimensionIndexStore dimensionIndexes;

  readonly RollupIndexStore rollupIndexes;

  bool started;

  readonly VisitorHasher visitorHasher;

  readonly EnrichmentPipeline enricher;

  readonly IngestionWorker[] workers;

  readonly QueryEngine queryEngine;

  readonly FunnelEngine funnelEngine;

  readonly ReportingCalendar calendar;

  readonly Counters counters = new();


  readonly Timer timer;

  readonly object housekeepingLock = new();

  readonly object disposalLock = new();

  readonly List<IDisposable> ownedStores = [];

  Task disposalTask;

  bool storageReleased;

  long sequence;

  long lastHousekeepingTicks;

  long lastRetentionTicks;

  readonly RetentionClock retentionClock;

  // The cutoffs up to which expired records were last removed from disk, per tier.
  RetentionCutoffs removedUpTo = RetentionCutoffs.None;

  int housekeepingRunning;

  int disposed;

  /// <summary>
  /// Opens or creates the analytics data directory and starts the ingestion workers.
  /// </summary>
  /// <remarks>
  /// The engine works on a copy of <paramref name="options"/>: changes made to them afterwards
  /// have no effect. Extensions, hooks and the logger are shared, not copied.
  /// </remarks>
  /// <exception cref="ArgumentException">The options are invalid.</exception>
  /// <exception cref="AnalyticsConfigurationException">The options conflict with the storage format of the data directory, or its metadata is damaged.</exception>
  /// <exception cref="IOException">Another engine has the data directory open.</exception>
  public AnalyticsEngine(AnalyticsOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    options = options.Clone();
    options.Validate();
    options.Storage.RollupIndexes.AddRollups(options.Rollups);
    this.options = options;
    logger = options.Storage.Logger ?? new ConsoleLogger();
    calendar = new ReportingCalendar(options.TimeZone, options.FirstDayOfWeek);
    var root = dataRoot = Path.GetFullPath(options.DataDirectory);
    Directory.CreateDirectory(root);
    // Storage reports errors on its own threads: count them like every other error.
    var storageLogger = new CountingLogger(logger, counters);
    retentionClock = new RetentionClock(options.Retention, calendar);
    var factory = new StoreFactory(options.Storage, root, storageLogger, retentionClock);

    directoryLock = new DirectoryLock(root);
    try
    {
      meta = new MetaStore(Path.Combine(root, "meta"));
      var isNew = ValidateConfiguration();
      var activations = RollupActivations.Register(meta, options.Rollups, isNew, root);
      sequence = Math.Max(
          DateTime.UtcNow.Ticks,
          meta.Get("raw-sequence") is { } stored ? long.Parse(stored, CultureInfo.InvariantCulture) : 0);

      visitorHasher = new VisitorHasher(
          options.Privacy.VisitorIdentification, options.Privacy.VisitorIdSecret, meta);
      enricher = new EnrichmentPipeline(options, visitorHasher, logger, counters);
      // Invalid store options fail here, not when a partition opens on an ingestion thread.
      factory.Validate(enricher.Schema);

      if (options.Sessions.Enabled)
      {
        var sessionResources = new DisposalGroup();
        ownedStores.Add(sessionResources);
        sessions = factory.OpenSessions(Path.Combine(root, "sessions"), enricher.Schema);
        sessionResources.Add(sessions);
        sessionsMaintainer = factory.CreateSessionsMaintainer(sessions);
        sessionResources.Add(sessionsMaintainer);
      }
      var definitions = options.Rollups.Definitions.ToArray();
      rollups = new RollupStore(
          Path.Combine(root, "rollups"), options.Rollups.Granularities, options.Storage.Partitioning, definitions, factory, storageLogger);
      ownedStores.Add(rollups);
      if (options.Storage.StoreRawEvents)
      {
        rawEvents = new RawEventStore(
            Path.Combine(root, "events"), options.Storage.Partitioning.RawEvents, factory, storageLogger);
        ownedStores.Add(rawEvents);
      }

      dimensionIndexes = new DimensionIndexStore(Path.Combine(root, "indexes"),
          options.Storage.DimensionIndexes, rawEvents, factory, meta, storageLogger);
      ownedStores.Add(dimensionIndexes);
      dimensionIndexes.Initialize();
      if (rawEvents != null) rawEvents.Indexes = dimensionIndexes;

      rollupIndexes = new RollupIndexStore(Path.Combine(root, "rollup-indexes"), options, rollups, factory, meta, storageLogger);
      ownedStores.Add(rollupIndexes);
      rollupIndexes.Initialize(rawEvents, activations, calendar, enricher.Schema);
      rollups.Indexes = rollupIndexes;

      var services = new IngestionServices
      {
        Options = options,
        Enricher = enricher,
        Rollups = rollups,
        RawEvents = rawEvents,
        DimensionIndexes = dimensionIndexes.Indexes.Count == 0 ? null : dimensionIndexes,
        Sessions = sessions,
        Calendar = calendar,
        Definitions = definitions,
        Counters = counters,
        Logger = logger,
        NextSequence = () => Interlocked.Increment(ref sequence),
        StoredSessionHorizon = GetStoredSessionHorizon()
      };
      workers = new IngestionWorker[options.Ingestion.WorkerCount];
      for (var i = 0; i < workers.Length; ++i)
        workers[i] = new IngestionWorker(services);

      queryEngine = new QueryEngine(
          options, calendar, rollups, rawEvents, definitions, activations, enricher);
      funnelEngine = new FunnelEngine(options, calendar, rawEvents, enricher);
      Web = new WebAnalytics(this);
      Storage = new AnalyticsStorage(rawEvents?.Store, rollups, sessions, sessionsMaintainer,
          () => ObjectDisposedException.ThrowIf(IsDisposed, this), dimensionIndexes, rollupIndexes);
      visitorHasher.RotateSalts(DateTime.UtcNow);

      // Create the timer disabled, then start background work inside the cleanup boundary.
      timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
      foreach (var worker in workers)
        worker.Start();
      lastHousekeepingTicks = DateTime.UtcNow.Ticks;
      var period = options.Ingestion.FlushInterval;
      timer.Change(period, period);
      started = true;
    }
    catch
    {
      Volatile.Write(ref disposed, 1);
      timer?.Dispose();
      try
      {
        if (workers != null)
        {
          foreach (var worker in workers)
            worker?.Stop();
          Task.WhenAll(workers.Where(x => x != null).Select(x => x.Completion)).GetAwaiter().GetResult();
        }
      }
      finally
      {
        DisposeStorage();
      }
      throw;
    }
  }

  /// <summary>
  /// Opens or creates an engine with default options in the given directory.
  /// </summary>
  public static AnalyticsEngine Open(string dataDirectory, Action<AnalyticsOptions> configure = null)
  {
    var options = new AnalyticsOptions { DataDirectory = dataDirectory };
    configure?.Invoke(options);
    return new AnalyticsEngine(options);
  }

  internal AnalyticsOptions Options => options;

  internal string NormalizeSite(string site) => enricher.NormalizeSite(site);

  internal SessionSchema Schema => enricher.Schema;

  internal RawEventStore RawEventStore => rawEvents;

  internal RollupStore RollupStore => rollups;

  internal DimensionIndexStore DimensionIndexes => dimensionIndexes;

  internal RollupIndexStore RollupIndexes => rollupIndexes;

  /// <summary>Counts integration failures without letting a failing logger affect the response.</summary>
  internal void ReportIntegrationError(Exception exception)
  {
    Interlocked.Increment(ref counters.Errors);
    try
    {
      logger.LogError(exception);
    }
    catch (Exception)
    {
      // Request tracking must preserve a response even when the configured logger fails.
    }
  }

  /// <summary>
  /// The default reporting time zone.
  /// </summary>
  public TimeZoneInfo TimeZone => options.TimeZone;

  /// <summary>
  /// The site used when events and queries do not specify one.
  /// </summary>
  public string DefaultSite => options.DefaultSite;

  /// <summary>
  /// Web analytics reports: summaries, time series and breakdowns.
  /// </summary>
  public WebAnalytics Web { get; }

  /// <summary>
  /// Advanced access to the engine's ZoneTrees and their existing maintainers.
  /// </summary>
  /// <remarks>
  /// The engine owns these resources. Release partition leases before disposing the engine;
  /// do not dispose or drop its trees, dispose its maintainers, or create additional maintainers.
  /// Direct writes bypass ingestion, aggregation and cached session state.
  /// </remarks>
  public AnalyticsStorage Storage { get; }

  bool IsDisposed => Volatile.Read(ref disposed) != 0;

  /// <summary>
  /// Persists the settings that define the storage format when the directory is created, and
  /// requires the same settings on reopen. Returns true for a new directory.
  /// </summary>
  bool ValidateConfiguration()
  {
    var walDirectory = options.Storage.WriteAheadLogDirectory == null
        ? ""
        : Path.GetFullPath(options.Storage.WriteAheadLogDirectory);
    (string Key, string Value, string Description)[] settings =
    [
      ("time-zone", options.TimeZone.Id, "time zone"),
      ("hll-precision", options.Rollups.HyperLogLogPrecision.ToString(CultureInfo.InvariantCulture), "HyperLogLog precision"),
      ("wal-directory", walDirectory, "write-ahead-log directory"),
    ];
    if (meta.Get("format-version") == null)
    {
      meta.Update(entries =>
      {
        foreach (var (key, value, _) in settings)
          entries[key] = value;
        entries["format-version"] = FormatVersion;
      });
      return true;
    }
    Require("format-version", FormatVersion, "format version");
    foreach (var (key, value, description) in settings)
      Require(key, value, description);
    return false;
  }

  void Require(string key, string expected, string description)
  {
    var stored = meta.Get(key);
    if (stored != expected)
      throw new AnalyticsConfigurationException(
          $"The analytics data directory was created with {description} '{stored}', " +
          $"but the options specify '{expected}'. This setting cannot be changed for an existing data directory.");
  }

  /// <summary>
  /// Events up to the returned time may continue a session stored by a previous run.
  /// The session store only holds unexpired sessions (TTL), so the scan is small.
  /// </summary>
  long GetStoredSessionHorizon()
  {
    if (sessions == null)
      return long.MinValue;
    var latest = long.MinValue;
    using var iterator = sessions.CreateIterator();
    while (iterator.Next())
      latest = Math.Max(latest, iterator.CurrentValue.LastTicks);
    return latest == long.MinValue ? latest : latest + options.Sessions.Timeout.Ticks;
  }

  bool TryCreateWorkItem(AnalyticsEvent analyticsEvent, out WorkItem item, out bool filtered)
  {
    ArgumentNullException.ThrowIfNull(analyticsEvent);
    analyticsEvent.Validate();
    item = default;
    filtered = false;
    if (IsDisposed)
      return false;
    if (!PassesFilters(analyticsEvent))
    {
      Interlocked.Increment(ref counters.EventsFiltered);
      filtered = true;
      return false;
    }
    item = new WorkItem(analyticsEvent.Snapshot(DateTime.UtcNow.Ticks));
    return true;
  }

  /// <summary>
  /// Runs the event filters. A filter that throws is logged and counted, and does not discard
  /// the event: analytics never breaks the caller.
  /// </summary>
  bool PassesFilters(AnalyticsEvent analyticsEvent)
  {
    foreach (var filter in options.EventFilters)
    {
      if (filter == null)
        continue;
      try
      {
        if (!filter.ShouldTrack(analyticsEvent))
          return false;
      }
      catch (Exception exception)
      {
        Interlocked.Increment(ref counters.Errors);
        logger.LogError(new InvalidOperationException(
            $"The event filter {filter.GetType().Name} threw an exception; the event is tracked.", exception));
      }
    }
    return true;
  }

  IngestionWorker Route(EventSnapshot analyticsEvent)
      => Route(enricher.NormalizeSite(analyticsEvent.Site), analyticsEvent.VisitorId, analyticsEvent.IpAddress, analyticsEvent.UserAgent);

  IngestionWorker Route(string site, string visitorId, string ipAddress = null, string userAgent = null)
  {
    if (workers.Length == 1)
      return workers[0];
    // Route by visitor identity so a visitor's events are processed by one worker, in order.
    var hash = !string.IsNullOrEmpty(visitorId)
        ? HashCode.Combine(site, visitorId)
        : HashCode.Combine(site, ipAddress ?? "", userAgent ?? "");
    return workers[(int)((uint)hash % (uint)workers.Length)];
  }

  /// <inheritdoc/>
  public bool Track(AnalyticsEvent analyticsEvent)
  {
    if (!TryCreateWorkItem(analyticsEvent, out var item, out _))
      return false;
    var writer = Route(item.Event).Writer;
    if (writer.TryWrite(item))
    {
      Interlocked.Increment(ref counters.EventsAccepted);
      return true;
    }
    if (options.Ingestion.QueueFullBehavior == QueueFullBehavior.Wait)
    {
      try
      {
        writer.WriteAsync(item).AsTask().GetAwaiter().GetResult();
        Interlocked.Increment(ref counters.EventsAccepted);
        return true;
      }
      catch (ChannelClosedException)
      {
        return false;
      }
    }
    Interlocked.Increment(ref counters.EventsDropped);
    return false;
  }

  /// <inheritdoc/>
  public async ValueTask<bool> TrackAsync(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken = default)
      => await TrackCoreAsync(analyticsEvent, wait: true, cancellationToken).ConfigureAwait(false) == IngestionResult.Accepted;

  internal ValueTask<IngestionResult> TrackRequestAsync(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    return TrackCoreAsync(analyticsEvent, options.Ingestion.QueueFullBehavior == QueueFullBehavior.Wait, cancellationToken);
  }

  async ValueTask<IngestionResult> TrackCoreAsync(AnalyticsEvent analyticsEvent, bool wait, CancellationToken cancellationToken)
  {
    if (!TryCreateWorkItem(analyticsEvent, out var item, out var filtered))
      return filtered ? IngestionResult.Filtered : IngestionResult.Unavailable;
    var writer = Route(item.Event).Writer;
    if (!wait)
    {
      if (writer.TryWrite(item))
      {
        Interlocked.Increment(ref counters.EventsAccepted);
        return IngestionResult.Accepted;
      }
      Interlocked.Increment(ref counters.EventsDropped);
      return IngestionResult.Unavailable;
    }
    try
    {
      await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
      Interlocked.Increment(ref counters.EventsAccepted);
      return IngestionResult.Accepted;
    }
    catch (ChannelClosedException)
    {
      return IngestionResult.Unavailable;
    }
  }

  /// <summary>
  /// Waits until every event queued before this call is processed and all buffered
  /// aggregates are written to storage.
  /// </summary>
  /// <remarks>
  /// A storage failure is reported to the caller. Pending aggregates are retained and retried
  /// by subsequent flushes, including automatic flushes, without applying successful writes twice.
  /// </remarks>
  public async Task FlushAsync(CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    var tasks = new List<Task>(workers.Length);
    foreach (var worker in workers)
    {
      var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      await worker.Writer.WriteAsync(new WorkItem(completion), cancellationToken).ConfigureAwait(false);
      tasks.Add(completion.Task);
    }
    await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc cref="FlushAsync"/>
  public void Flush(CancellationToken cancellationToken = default) =>
      FlushAsync(cancellationToken).GetAwaiter().GetResult();

  /// <summary>
  /// Flushes, moves the in-memory data of every open store to disk and waits until the
  /// background merges complete.
  /// </summary>
  /// <remarks>
  /// The engine compacts on its own as data grows (see
  /// <see cref="StoreOptions.MutableSegmentMaxItemCount"/>), so this is never required. Call it
  /// after a bulk import to release memory, or to measure queries against data on disk.
  /// Tracking continues meanwhile.
  /// </remarks>
  public async Task CompactAsync(CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    await FlushAsync(cancellationToken).ConfigureAwait(false);
    await Task.Run(() =>
    {
      rawEvents?.Store.Compact();
      foreach (var index in dimensionIndexes.Indexes) index.Store.Compact();
      foreach (var store in rollups.AllStores)
        store.Compact();
      if (sessionsMaintainer != null)
      {
        sessionsMaintainer.EvictToDisk();
        sessionsMaintainer.WaitForBackgroundThreads();
      }
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc cref="CompactAsync"/>
  public void Compact(CancellationToken cancellationToken = default) =>
      CompactAsync(cancellationToken).GetAwaiter().GetResult();

  /// <summary>
  /// Executes a query.
  /// </summary>
  /// <remarks>
  /// Rollup-based answers include events up to the last flush (see
  /// <see cref="IngestionOptions.FlushInterval"/>). Call <see cref="FlushAsync"/> first
  /// when the result must include every tracked event.
  /// </remarks>
  /// <exception cref="AnalyticsQueryException">The query is invalid or cannot be answered.</exception>
  /// <exception cref="AnalyticsQueryTimeoutException">The query ran longer than <see cref="QueryOptions.Timeout"/>.</exception>
  public AnalyticsResult Query(AnalyticsQuery query, CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    return WithTimeout(token => queryEngine.Execute(query, token), cancellationToken);
  }

  /// <summary>
  /// Executes a query on a thread-pool thread.
  /// </summary>
  /// <inheritdoc cref="Query(AnalyticsQuery, CancellationToken)"/>
  public Task<AnalyticsResult> QueryAsync(AnalyticsQuery query, CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    ArgumentNullException.ThrowIfNull(query);
    return Task.Run(() => Query(query, cancellationToken), cancellationToken);
  }

  /// <summary>
  /// Analyzes a funnel: how many visitors completed each step, in order. Funnels are answered
  /// from raw events.
  /// </summary>
  /// <exception cref="AnalyticsQueryException">The funnel is invalid or raw events are not stored.</exception>
  /// <exception cref="AnalyticsQueryTimeoutException">The funnel ran longer than <see cref="QueryOptions.Timeout"/>.</exception>
  public FunnelResult Query(FunnelQuery funnel, CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    ArgumentNullException.ThrowIfNull(funnel);
    return WithTimeout(token => funnelEngine.Execute(funnel, token), cancellationToken);
  }

  /// <summary>
  /// Analyzes a funnel on a thread-pool thread.
  /// </summary>
  /// <inheritdoc cref="Query(FunnelQuery, CancellationToken)"/>
  public Task<FunnelResult> QueryAsync(FunnelQuery funnel, CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    ArgumentNullException.ThrowIfNull(funnel);
    return Task.Run(() => Query(funnel, cancellationToken), cancellationToken);
  }

  /// <summary>
  /// Runs a query with <see cref="QueryOptions.Timeout"/> applied on top of the caller's token.
  /// </summary>
  T WithTimeout<T>(Func<CancellationToken, T> execute, CancellationToken cancellationToken)
  {
    var timeout = options.Queries.Timeout;
    if (timeout == null)
      return execute(cancellationToken);
    using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    source.CancelAfter(timeout.Value);
    try
    {
      return execute(source.Token);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && source.IsCancellationRequested)
    {
      throw new AnalyticsQueryTimeoutException(timeout.Value);
    }
  }

  /// <summary>
  /// Lists the event names recorded for a site in a range, including <see cref="StandardEvents.Session"/>.
  /// </summary>
  /// <remarks>
  /// Partial rollup buckets are resolved from raw events when stored. Without raw events,
  /// discovery uses whole available buckets, matching rollup-only query fallback behavior.
  /// </remarks>
  /// <param name="range">The time range.</param>
  /// <param name="site">The site; null for <see cref="AnalyticsOptions.DefaultSite"/>.</param>
  /// <param name="timeZone">The time zone of the range; null for the engine's reporting time zone.</param>
  /// <param name="cancellationToken">Cancels the operation.</param>
  public IReadOnlyList<string> GetEventNames(
      DateRange range, string site = null, TimeZoneInfo timeZone = null, CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    cancellationToken.ThrowIfCancellationRequested();
    return WithTimeout(token => queryEngine.GetEventNames(range, site, timeZone, token), cancellationToken);
  }

  /// <summary>
  /// Lists the event names recorded for a site in a range on a thread-pool thread.
  /// </summary>
  /// <inheritdoc cref="GetEventNames"/>
  public Task<IReadOnlyList<string>> GetEventNamesAsync(
      DateRange range, string site = null, TimeZoneInfo timeZone = null, CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    return Task.Run(() => GetEventNames(range, site, timeZone, cancellationToken), cancellationToken);
  }

  /// <summary>
  /// Counts distinct visitors seen in the last <paramref name="window"/>, from raw events.
  /// Raw events are written immediately, so the count is real-time. Returns null when raw
  /// events are not stored.
  /// </summary>
  internal long? GetCurrentVisitors(TimeSpan window, string site, CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    if (rawEvents == null)
      return null;
    site = enricher.NormalizeSite(site);
    var now = DateTime.UtcNow.Ticks;
    var slices = rawEvents.Scan(
        site, now - window.Ticks, now + options.Ingestion.MaxFutureSkew.Ticks, options.Queries.MaxDegreeOfParallelism,
        () => new HashSet<ulong>(),
        (visitors, _, value) =>
        {
          var visitor = RawEventCodec.ReadVisitor(value);
          if (visitor != 0)
            visitors.Add(visitor);
        },
        cancellationToken);
    if (slices.Length == 0)
      return 0;
    var all = slices.MaxBy(x => x.Count);
    foreach (var visitors in slices)
    {
      if (visitors != all)
        all.UnionWith(visitors);
    }
    return all.Count;
  }

  /// <summary>
  /// Deletes every raw event and the active session of an explicitly identified visitor,
  /// for example to honor a data erasure request. Aggregated rollups contain no
  /// per-visitor data and are not modified. Returns the number of deleted events.
  /// </summary>
  /// <param name="visitorId">The visitor id the events were tracked with (<see cref="AnalyticsEvent.VisitorId"/>).</param>
  /// <param name="site">The site; null for <see cref="AnalyticsOptions.DefaultSite"/>.</param>
  /// <param name="cancellationToken">Cancels the operation.</param>
  public long EraseVisitor(string visitorId, string site = null, CancellationToken cancellationToken = default)
      => EraseVisitorAsync(visitorId, site, cancellationToken).GetAwaiter().GetResult();

  /// <inheritdoc cref="EraseVisitor"/>
  public async Task<long> EraseVisitorAsync(string visitorId, string site = null, CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    ArgumentException.ThrowIfNullOrEmpty(visitorId);
    await FlushAsync(cancellationToken).ConfigureAwait(false);
    site = enricher.NormalizeSite(site);
    var hash = visitorHasher.HashVisitorId(site, visitorId);
    if (hash == 0)
      return 0;
    var worker = Route(site, visitorId);
    var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
    var erasure = new VisitorErasure(hash, () => EraseFlushedVisitor(hash, site, cancellationToken), completion);
    await worker.Writer.WriteAsync(new WorkItem(erasure), cancellationToken).ConfigureAwait(false);
    return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
  }

  long EraseFlushedVisitor(ulong hash, string site, CancellationToken cancellationToken)
  {
    using var suspension = dimensionIndexes.Suspend();
    try { return EraseFlushedVisitorCore(hash, site, cancellationToken); }
    catch { dimensionIndexes.Invalidate(); throw; }
  }

  long EraseFlushedVisitorCore(ulong hash, string site, CancellationToken cancellationToken)
  {
    long deleted = 0;
    if (rawEvents != null)
    {
      var store = rawEvents.Store;
      var sitePrefix = KeyCodec.SitePrefix(site);
      var partitions = store.ListPartitions();
      // Partitions are independent; each is scanned and cleaned on its own thread.
      ParallelWork.For(partitions.Count, options.Queries.MaxDegreeOfParallelism, i =>
      {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = store.Acquire(partitions[i]);
        if (lease == null)
          return;
        using var indexWriter = dimensionIndexes.CreateWriter();
        var toDelete = new List<(Memory<byte> Key, Memory<byte> Value)>();
        // Keys are collected first and deleted after the scan, so a plain iterator suffices.
        using (var iterator = lease.Tree.CreateIterator())
        {
          iterator.Seek(sitePrefix);
          while (iterator.Next())
          {
            if (!iterator.CurrentKey.Span.StartsWith(sitePrefix))
              break;
            if (RawEventCodec.ReadVisitor(iterator.CurrentValue.Span) == hash)
              toDelete.Add((iterator.CurrentKey, iterator.CurrentValue));
          }
        }
        foreach (var (key, value) in toDelete)
        {
          var record = RawEventCodec.Decode(key.Span, value.Span);
          indexWriter.Delete(record);
          lease.Tree.ForceDelete(key);
        }
        Interlocked.Add(ref deleted, toDelete.Count);
      });
    }
    return deleted;
  }

  /// <summary>
  /// Applies retention immediately: drops the partitions that expired as a whole, removes the
  /// expired records of the others from disk, and destroys old salts. Queries never see expired
  /// data, with or without this call; it reclaims disk space. The engine does this on its own
  /// every <see cref="RetentionOptions.CheckInterval"/>. <see cref="AnalyticsStatistics.PartitionsDropped"/>
  /// counts the dropped partitions.
  /// </summary>
  public void ApplyRetention(CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    cancellationToken.ThrowIfCancellationRequested();
    lock (housekeepingLock)
      ApplyRetentionCore();
  }

  /// <summary>
  /// Applies retention on a thread-pool thread.
  /// </summary>
  /// <inheritdoc cref="ApplyRetention"/>
  public Task ApplyRetentionAsync(CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    return Task.Run(() => ApplyRetention(cancellationToken), cancellationToken);
  }

  int ApplyRetentionCore()
  {
    var now = DateTime.UtcNow;
    retentionClock.Refresh(now.Ticks);
    var cutoffs = retentionClock.Current;
    var dropped = 0;
    if (rawEvents != null && cutoffs.RawEvents != long.MinValue)
      dropped += rawEvents.Store.DropPartitionsEndingBefore(cutoffs.RawEvents);
    if (cutoffs.RawEvents != long.MinValue)
      foreach (var index in dimensionIndexes.Indexes)
        dropped += index.Store.DropPartitionsEndingBefore(cutoffs.RawEvents);
    foreach (var granularity in RollupStore.Granularities)
    {
      if (!rollups.IsEnabled(granularity) || cutoffs.Of(granularity) == long.MinValue)
        continue;
      // Daily partitions are named after local days of the reporting time zone.
      var cutoff = granularity == Granularity.Day ? cutoffs.DayRollupsLocalDay : cutoffs.Of(granularity);
      dropped += rollups.GetStore(granularity).DropPartitionsEndingBefore(cutoff);
    }
    RemoveExpiredRecords(cutoffs);
    dropped += rollupIndexes.ApplyRetention(cutoffs, retentionClock);
    visitorHasher.RotateSalts(now);
    Interlocked.Add(ref counters.PartitionsDropped, dropped);
    lastRetentionTicks = now.Ticks;
    return dropped;
  }

  /// <summary>
  /// Removes expired records from disk in the partitions that remain, for every tier whose
  /// cutoff moved since the last time: cutoffs move in whole days, so each remaining partition
  /// rewrites at most the oldest part of each key range once a day.
  /// </summary>
  void RemoveExpiredRecords(RetentionCutoffs cutoffs)
  {
    if (rawEvents != null && cutoffs.RawEvents != long.MinValue && cutoffs.RawEvents != removedUpTo.RawEvents)
    {
      rawEvents.Store.RemoveExpired(
          cutoffs.RawEvents, tree => ExpiredRecords.DeleteFirstOfExpiredHeads(tree, 1, retentionClock.IsRawEventExpired));
      foreach (var index in dimensionIndexes.Indexes)
        index.Store.RemoveExpired(cutoffs.RawEvents,
            tree => DimensionIndexKeyCodec.DeleteFirstExpired(tree, retentionClock.IsRawEventExpired));
      removedUpTo = removedUpTo with { RawEvents = cutoffs.RawEvents };
    }
    foreach (var granularity in RollupStore.Granularities)
    {
      var cutoff = granularity == Granularity.Day ? cutoffs.DayRollupsLocalDay : cutoffs.Of(granularity);
      var removed = granularity == Granularity.Day ? removedUpTo.DayRollupsLocalDay : removedUpTo.Of(granularity);
      if (!rollups.IsEnabled(granularity) || cutoff == long.MinValue || cutoff == removed)
        continue;
      rollups.GetStore(granularity).RemoveExpired(
          cutoff, tree => ExpiredRecords.DeleteFirstOfExpiredHeads(tree, 3, key => retentionClock.IsRollupExpired(granularity, key)));
      removedUpTo = granularity switch
      {
        Granularity.Minute => removedUpTo with { MinuteRollups = cutoff },
        Granularity.Hour => removedUpTo with { HourRollups = cutoff },
        _ => removedUpTo with { DayRollups = cutoffs.DayRollups, DayRollupsLocalDay = cutoff }
      };
    }
  }

  /// <summary>
  /// Starts a new partition in every tier whose latest partition holds its configured number of
  /// records. A new partition starts at a day boundary later than any event the engine can
  /// still receive: events up to <see cref="IngestionOptions.MaxFutureSkew"/> ahead, whose daily
  /// rollup buckets are local days up to 14 hours ahead of UTC. Returns the number of started
  /// partitions.
  /// </summary>
  internal int RollOverPartitions(long nowUtcTicks)
  {
    var earliestStart = nowUtcTicks + options.Ingestion.MaxFutureSkew.Ticks + TimeSpan.TicksPerDay;
    var started = 0;
    if (rawEvents != null && rawEvents.Store.RollOver(earliestStart))
      ++started;
    foreach (var index in dimensionIndexes.Indexes)
      if (index.Store.RollOver(earliestStart)) ++started;
    foreach (var store in rollups.AllStores)
    {
      if (store.RollOver(earliestStart))
        ++started;
    }
    return started;
  }

  /// <summary>
  /// Returns a snapshot of the engine counters.
  /// </summary>
  public AnalyticsStatistics GetStatistics() => GetStatistics(includePartitions: true);

  /// <summary>
  /// Returns engine counters, optionally including storage partition details.
  /// Pass false for lightweight progress polling without inspecting partitions.
  /// </summary>
  public AnalyticsStatistics GetStatistics(bool includePartitions)
  {
    var openPartitions = includePartitions ? rollups.AllStores.Sum(x => x.OpenPartitionCount) +
        (rawEvents?.Store.OpenPartitionCount ?? 0) + dimensionIndexes.Indexes.Sum(x => x.Store.OpenPartitionCount) : 0;
    return new AnalyticsStatistics
    {
      EventsAccepted = Interlocked.Read(ref counters.EventsAccepted),
      EventsDropped = Interlocked.Read(ref counters.EventsDropped),
      EventsProcessed = Interlocked.Read(ref counters.EventsProcessed),
      EventsRejected = Interlocked.Read(ref counters.EventsRejected),
      EventsFiltered = Interlocked.Read(ref counters.EventsFiltered),
      BotsExcluded = Interlocked.Read(ref counters.BotsExcluded),
      BotsTracked = Interlocked.Read(ref counters.BotsTracked),
      SessionsStarted = Interlocked.Read(ref counters.SessionsStarted),
      Flushes = Interlocked.Read(ref counters.Flushes),
      Errors = Interlocked.Read(ref counters.Errors),
      PartitionsDropped = Interlocked.Read(ref counters.PartitionsDropped),
      QueueLength = workers.Sum(x => (long)x.QueueLength),
      OpenPartitions = openPartitions,
      Partitions = includePartitions ? DescribePartitions() : []
    };
  }

  List<PartitionStatistics> DescribePartitions()
  {
    var result = new List<PartitionStatistics>();
    void Add<TValue>(PartitionedStore<Memory<byte>, TValue> store)
    {
      var tier = Path.GetRelativePath(dataRoot, store.RootDirectory).Replace('\\', '/');
      foreach (var (name, count) in store.Describe())
        result.Add(new PartitionStatistics { Directory = tier + "/" + name, ApproximateRecordCount = count });
    }
    if (rawEvents != null)
      Add(rawEvents.Store);
    foreach (var index in dimensionIndexes.Indexes) Add(index.Store);
    foreach (var store in rollups.AllStores)
      Add(store);
    return result;
  }

  void OnTimer(object state)
  {
    if (IsDisposed)
      return;
    // Ask idle workers to flush. Busy workers flush on their own schedule.
    foreach (var worker in workers)
      worker.Writer.TryWrite(WorkItem.BackgroundFlush);

    var now = DateTime.UtcNow.Ticks;
    if (now - lastHousekeepingTicks < TimeSpan.TicksPerMinute)
      return;
    if (Interlocked.Exchange(ref housekeepingRunning, 1) == 1)
      return;
    lastHousekeepingTicks = now;
    Task.Run(() =>
    {
      try
      {
        lock (housekeepingLock)
        {
          if (IsDisposed)
            return;
          PersistSequence();
          var current = DateTime.UtcNow.Ticks;
          // Expired records are hidden as soon as a cutoff passes them, between retention runs too.
          retentionClock.Refresh(current);
          var idle = options.Storage.PartitionIdleTimeout;
          // Ingestion writes to the latest raw partition continuously.
          rawEvents?.Store.CloseIdle(idle, keepLatestOpen: true);
          foreach (var index in dimensionIndexes.Indexes) index.Store.CloseIdle(idle, keepLatestOpen: true);
          foreach (var store in rollups.AllStores)
            store.CloseIdle(idle, keepLatestOpen: false);
          RollOverPartitions(current);
          var interval = options.Retention.CheckInterval;
          if (interval.HasValue && current - lastRetentionTicks >= interval.Value.Ticks)
            ApplyRetentionCore();
        }
      }
      catch (Exception e)
      {
        Interlocked.Increment(ref counters.Errors);
        logger.LogError(e);
      }
      finally
      {
        Volatile.Write(ref housekeepingRunning, 0);
      }
    });
  }

  /// <summary>
  /// Persists the raw event sequence, every minute and at shutdown. On restart the sequence
  /// resumes from the larger of the stored value and the current UTC ticks, so the stored value
  /// only matters if the clock moves backwards between runs.
  /// </summary>
  void PersistSequence() =>
      meta.Set("raw-sequence", Interlocked.Read(ref sequence).ToString(CultureInfo.InvariantCulture));

  /// <summary>
  /// Stops accepting events, processes queued events, flushes and closes all stores.
  /// </summary>
  /// <remarks>
  /// A final flush or cleanup failure is reported. If cleanup fails, the directory lock stays
  /// held and a later disposal retries the remaining resources. Concurrent disposals share the attempt.
  /// <see cref="DisposeAsync"/> schedules cleanup off the calling thread, including retry attempts.
  /// </remarks>
  public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

  /// <inheritdoc cref="Dispose"/>
  public ValueTask DisposeAsync()
  {
    lock (disposalLock)
    {
      if (disposalTask == null)
      {
        Volatile.Write(ref disposed, 1);
        disposalTask = Task.Run(() => DisposeCoreAsync(stopWorkers: true));
      }
      else if (disposalTask.IsCompleted)
      {
        if (storageReleased)
          return ValueTask.CompletedTask;
        disposalTask = Task.Run(() => DisposeCoreAsync(stopWorkers: false));
      }
      return new ValueTask(disposalTask);
    }
  }

  async Task DisposeCoreAsync(bool stopWorkers)
  {
    if (!stopWorkers)
    {
      lock (housekeepingLock)
        DisposeStorage();
      return;
    }
    timer?.Dispose();
    try
    {
      await Task.WhenAll(workers.Select(x => x.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
    finally
    {
      lock (housekeepingLock)
      {
        try
        {
          PersistSequence();
        }
        finally
        {
          DisposeStorage();
        }
      }
    }
  }

  void DisposeStorage()
  {
    Disposal.DisposeAll(ownedStores);
    // Release only after every resource confirms closure. Failed cleanup remains owned for retry.
    try
    {
      if (started)
      {
        dimensionIndexes.MarkClean();
        rollupIndexes.MarkClean();
      }
    }
    finally
    {
      directoryLock?.Dispose();
      storageReleased = true;
    }
  }
}
