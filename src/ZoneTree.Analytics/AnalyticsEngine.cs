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
/// Create one engine per data directory and keep it for the application lifetime.
/// All members are thread-safe. <see cref="Track"/> never blocks by default: events are
/// queued and processed by background workers.
/// </para>
/// <para>
/// Storage layout under <see cref="AnalyticsOptions.DataDirectory"/>, with the default
/// <see cref="PartitioningOptions"/>:
/// <code>
/// meta/                       configuration, secrets, salts
/// sessions/                   active sessions
/// events/2026-10/             raw events, one ZoneTree per month
/// rollups/minute/2026-10-08/  minute rollups, one ZoneTree per day
/// rollups/hour/2026-10/       hourly rollups, one ZoneTree per month
/// rollups/day/2026/           daily rollups, one ZoneTree per year
/// </code>
/// </para>
/// </remarks>
public sealed class AnalyticsEngine : IDisposable, IAsyncDisposable
{
  const string FormatVersion = "1";

  readonly AnalyticsOptions options;

  readonly ILogger logger;

  readonly MetaStore meta;

  readonly IZoneTree<ulong, SessionState> sessions;

  readonly IMaintainer sessionsMaintainer;

  readonly RollupStore rollups;

  readonly RawEventStore rawEvents;

  readonly VisitorHasher visitorHasher;

  readonly EnrichmentPipeline enricher;

  readonly IngestionWorker[] workers;

  readonly QueryEngine queryEngine;

  readonly FunnelEngine funnelEngine;

  readonly ReportingCalendar calendar;

  readonly Counters counters = new();

  readonly Dictionary<string, long> definitionActiveSince = new(StringComparer.Ordinal);

  readonly Timer timer;

  readonly object housekeepingLock = new();

  long sequence;

  long lastHousekeepingTicks;

  long lastRetentionTicks;

  int housekeepingRunning;

  int disposed;

  /// <summary>
  /// Opens or creates the analytics data directory and starts the ingestion workers.
  /// </summary>
  /// <remarks>
  /// The engine works on a copy of <paramref name="options"/>: changes made to them afterwards
  /// have no effect. Extensions, hooks and the logger are shared, not copied.
  /// </remarks>
  public AnalyticsEngine(AnalyticsOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    options = options.Clone();
    options.Validate();
    this.options = options;
    logger = options.Storage.Logger ?? new ConsoleLogger();
    calendar = new ReportingCalendar(options.TimeZone, options.FirstDayOfWeek);
    var root = Path.GetFullPath(options.DataDirectory);
    Directory.CreateDirectory(root);
    var factory = new StoreFactory(options.Storage, logger);

    meta = new MetaStore(factory.OpenMeta(Path.Combine(root, "meta")));
    try
    {
      var isNew = ValidateConfiguration();
      RegisterRollupDefinitions(isNew);
      sequence = Math.Max(
          DateTime.UtcNow.Ticks,
          meta.Get("raw-sequence") is { } stored ? long.Parse(stored, CultureInfo.InvariantCulture) : 0);

      visitorHasher = new VisitorHasher(
          options.Privacy.VisitorIdentification, options.Privacy.VisitorIdSecret, meta);
      enricher = new EnrichmentPipeline(options, visitorHasher, logger, counters);

      if (options.Sessions.Enabled)
      {
        sessions = factory.OpenSessions(Path.Combine(root, "sessions"), enricher.Schema);
        sessionsMaintainer = factory.CreateMaintainer(sessions);
      }
      var definitions = options.Rollups.Definitions.ToArray();
      rollups = new RollupStore(
          Path.Combine(root, "rollups"), options.Rollups.Granularities, options.Storage.Partitioning, definitions, factory, logger);
      if (options.Storage.StoreRawEvents)
      {
        rawEvents = new RawEventStore(
            Path.Combine(root, "events"), options.Storage.Partitioning.RawEvents, factory, logger);
      }

      var services = new IngestionServices
      {
        Options = options,
        Enricher = enricher,
        Rollups = rollups,
        RawEvents = rawEvents,
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
          options, calendar, rollups, rawEvents, definitions, GetDefinitionActiveSince, enricher);
      funnelEngine = new FunnelEngine(options, calendar, rawEvents, enricher);
      Web = new WebAnalytics(this);
      visitorHasher.RotateSalts(DateTime.UtcNow);
    }
    catch
    {
      DisposeStorage();
      throw;
    }

    // Background work starts only once the engine is fully constructed.
    foreach (var worker in workers)
      worker.Start();
    lastHousekeepingTicks = DateTime.UtcNow.Ticks;
    var period = options.Ingestion.FlushInterval;
    timer = new Timer(OnTimer, null, period, period);
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

  internal SessionSchema Schema => enricher.Schema;

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

  bool IsDisposed => Volatile.Read(ref disposed) != 0;

  /// <summary>
  /// Persists the settings that define the storage format when the directory is created, and
  /// requires the same settings on reopen. Returns true for a new directory.
  /// </summary>
  bool ValidateConfiguration()
  {
    var partitioning = options.Storage.Partitioning;
    (string Key, string Value, string Description)[] settings =
    [
      ("time-zone", options.TimeZone.Id, "time zone"),
      ("hll-precision", options.Rollups.HyperLogLogPrecision.ToString(CultureInfo.InvariantCulture), "HyperLogLog precision"),
      ("raw-partitioning", partitioning.RawEvents.ToString(), "raw event partitioning"),
      ("minute-partitioning", partitioning.MinuteRollups.ToString(), "minute rollup partitioning"),
      ("hour-partitioning", partitioning.HourRollups.ToString(), "hourly rollup partitioning"),
      ("day-partitioning", partitioning.DayRollups.ToString(), "daily rollup partitioning"),
    ];
    if (meta.Get("format-version") == null)
    {
      foreach (var (key, value, _) in settings)
        meta.Set(key, value);
      // Written last, so a directory whose creation was interrupted is simply created again.
      meta.Set("format-version", FormatVersion);
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
  /// Records when each rollup definition became active. A definition added to an existing
  /// data directory, re-added after it was removed, or whose events changed only covers data
  /// recorded after that time; earlier ranges are answered from raw events.
  /// </summary>
  void RegisterRollupDefinitions(bool isNewDirectory)
  {
    // A definition that is not configured is not maintained. Forget it, so that adding it
    // again starts a new activation instead of claiming coverage of the time it was missing.
    var configured = options.Rollups.Definitions.Select(RollupActivationKey).ToHashSet(StringComparer.Ordinal);
    foreach (var (key, _) in meta.ListByPrefix(RollupActivationPrefix))
    {
      if (!configured.Contains(key))
        meta.Delete(key);
    }
    foreach (var definition in options.Rollups.Definitions)
    {
      var key = RollupActivationKey(definition);
      var stored = meta.Get(key);
      long since;
      if (stored != null)
      {
        since = long.Parse(stored, CultureInfo.InvariantCulture);
      }
      else
      {
        since = isNewDirectory ? 0 : DateTime.UtcNow.Ticks;
        meta.Set(key, since.ToString(CultureInfo.InvariantCulture));
      }
      definitionActiveSince[definition.Id] = since;
    }
  }

  const string RollupActivationPrefix = "rollup:";

  /// <summary>
  /// The meta key of a definition's activation time. It covers everything that decides which
  /// rows the definition holds: its dimensions and its event restriction. Changing either starts
  /// a new activation.
  /// </summary>
  static string RollupActivationKey(RollupDefinition definition)
  {
    var key = RollupActivationPrefix + definition.Id;
    if (definition.Events == null)
      return key;
    var events = definition.Events.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    return key + KeyCodec.ValueSeparator + "events:" + string.Join(KeyCodec.ValueSeparator, events);
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

  long GetDefinitionActiveSince(string id) =>
      definitionActiveSince.TryGetValue(id, out var since) ? since : long.MaxValue;

  bool TryCreateWorkItem(AnalyticsEvent analyticsEvent, out WorkItem item)
  {
    ArgumentNullException.ThrowIfNull(analyticsEvent);
    analyticsEvent.Validate();
    item = default;
    if (IsDisposed)
      return false;
    if (!PassesFilters(analyticsEvent))
    {
      Interlocked.Increment(ref counters.EventsFiltered);
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
  {
    if (workers.Length == 1)
      return workers[0];
    // Route by visitor identity so a visitor's events are processed by one worker, in order.
    var hash = analyticsEvent.VisitorId != null
        ? HashCode.Combine(analyticsEvent.Site, analyticsEvent.VisitorId)
        : HashCode.Combine(analyticsEvent.Site, analyticsEvent.IpAddress, analyticsEvent.UserAgent);
    return workers[(int)((uint)hash % (uint)workers.Length)];
  }

  /// <summary>
  /// Queues an event for processing and returns immediately.
  /// </summary>
  /// <remarks>
  /// The event is copied, so it can be modified or reused once this method returns.
  /// <see cref="AnalyticsOptions.EventFilters"/> run here, on the calling thread.
  /// </remarks>
  /// <returns>
  /// True if the event was queued. False if it was filtered out, the engine is disposed, or
  /// the queue is full and <see cref="QueueFullBehavior.Drop"/> is configured.
  /// </returns>
  /// <exception cref="ArgumentException">The event name is missing or reserved.</exception>
  public bool Track(AnalyticsEvent analyticsEvent)
  {
    if (!TryCreateWorkItem(analyticsEvent, out var item))
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

  /// <summary>
  /// Queues an event, waiting asynchronously for queue space when the queue is full.
  /// </summary>
  /// <returns>True if the event was queued; false if it was filtered out or the engine is disposed.</returns>
  /// <exception cref="ArgumentException">The event name is missing or reserved.</exception>
  public async ValueTask<bool> TrackAsync(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken = default)
  {
    if (!TryCreateWorkItem(analyticsEvent, out var item))
      return false;
    try
    {
      await Route(item.Event).Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
      Interlocked.Increment(ref counters.EventsAccepted);
      return true;
    }
    catch (ChannelClosedException)
    {
      return false;
    }
  }

  /// <summary>
  /// Waits until every event queued before this call is processed and all buffered
  /// aggregates are written to storage.
  /// </summary>
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
    PersistSequence();
  }

  /// <summary>
  /// Synchronous version of <see cref="FlushAsync"/>.
  /// </summary>
  public void Flush() => FlushAsync().GetAwaiter().GetResult();

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
    return Task.Run(() => WithTimeout(token => queryEngine.Execute(query, token), cancellationToken), cancellationToken);
  }

  /// <summary>
  /// Analyzes a funnel: how many visitors completed each step, in order.
  /// </summary>
  /// <exception cref="AnalyticsQueryException">The funnel is invalid or raw events are not available.</exception>
  /// <exception cref="AnalyticsQueryTimeoutException">The funnel ran longer than <see cref="QueryOptions.Timeout"/>.</exception>
  public Task<FunnelResult> QueryFunnelAsync(FunnelQuery funnel, CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    ArgumentNullException.ThrowIfNull(funnel);
    return Task.Run(() => WithTimeout(token => funnelEngine.Execute(funnel, token), cancellationToken), cancellationToken);
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
  public IReadOnlyList<string> GetEventNames(DateRange range, string site = null, TimeZoneInfo timeZone = null)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    return queryEngine.GetEventNames(range, site, timeZone);
  }

  /// <summary>
  /// Counts distinct visitors seen in the last <paramref name="window"/>, from raw events.
  /// Raw events are written immediately, so the count is real-time. Returns null when raw
  /// events are not stored.
  /// </summary>
  internal long? GetCurrentVisitors(TimeSpan window, string site = null)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    if (rawEvents == null)
      return null;
    site = enricher.NormalizeSite(site);
    var now = DateTime.UtcNow.Ticks;
    var visitors = new HashSet<ulong>();
    rawEvents.Scan(site, now - window.Ticks, now + options.Ingestion.MaxFutureSkew.Ticks, (_, _, value) =>
    {
      var visitor = RawEventCodec.ReadVisitor(value.Span);
      if (visitor != 0)
        visitors.Add(visitor);
      return true;
    });
    return visitors.Count;
  }

  /// <summary>
  /// Deletes every raw event and the active session of an explicitly identified visitor,
  /// for example to honor a data erasure request. Aggregated rollups contain no
  /// per-visitor data and are not modified. Returns the number of deleted events.
  /// </summary>
  public async Task<long> EraseVisitorAsync(string visitorId, string site = null, CancellationToken cancellationToken = default)
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    ArgumentException.ThrowIfNullOrEmpty(visitorId);
    await FlushAsync(cancellationToken).ConfigureAwait(false);
    site = enricher.NormalizeSite(site);
    var hash = visitorHasher.HashVisitorId(site, visitorId);
    if (hash == 0)
      return 0;
    long deleted = 0;
    if (rawEvents != null)
    {
      var store = rawEvents.Store;
      var sitePrefix = KeyCodec.SitePrefix(site);
      foreach (var partition in store.ListPartitions())
      {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = store.Acquire(partition, create: false);
        if (lease == null)
          continue;
        var toDelete = new List<Memory<byte>>();
        using (var iterator = lease.Tree.CreateIterator(IteratorType.Snapshot))
        {
          iterator.Seek(sitePrefix);
          while (iterator.Next())
          {
            if (!iterator.CurrentKey.Span.StartsWith(sitePrefix))
              break;
            if (RawEventCodec.ReadVisitor(iterator.CurrentValue.Span) == hash)
              toDelete.Add(iterator.CurrentKey);
          }
        }
        foreach (var key in toDelete)
          lease.Tree.ForceDelete(key);
        deleted += toDelete.Count;
      }
    }
    sessions?.ForceDelete(hash);
    return deleted;
  }

  /// <summary>
  /// Applies retention immediately: drops expired partitions and destroys old salts.
  /// Returns the number of dropped partitions.
  /// </summary>
  public int ApplyRetention()
  {
    ObjectDisposedException.ThrowIf(IsDisposed, this);
    lock (housekeepingLock)
      return ApplyRetentionCore();
  }

  int ApplyRetentionCore()
  {
    var now = DateTime.UtcNow;
    var cutoffs = RetentionCutoffs.Compute(options.Retention, calendar, now.Ticks);
    var dropped = 0;
    if (rawEvents != null && cutoffs.RawEvents != long.MinValue)
      dropped += rawEvents.Store.DropPartitionsEndingBefore(cutoffs.RawEvents);
    foreach (var granularity in RollupStore.Granularities)
    {
      if (!rollups.IsEnabled(granularity) || cutoffs.Of(granularity) == long.MinValue)
        continue;
      // Daily partitions are named after local days of the reporting time zone.
      var cutoff = granularity == Granularity.Day ? cutoffs.DayRollupsLocalDay : cutoffs.Of(granularity);
      dropped += rollups.GetStore(granularity).DropPartitionsEndingBefore(cutoff);
    }
    visitorHasher.RotateSalts(now);
    Interlocked.Add(ref counters.PartitionsDropped, dropped);
    lastRetentionTicks = now.Ticks;
    return dropped;
  }

  /// <summary>
  /// Returns a snapshot of the engine counters.
  /// </summary>
  public AnalyticsStatistics GetStatistics()
  {
    var openPartitions = rollups.AllStores.Sum(x => x.OpenPartitionCount) +
        (rawEvents?.Store.OpenPartitionCount ?? 0);
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
      OpenPartitions = openPartitions
    };
  }

  void OnTimer(object state)
  {
    if (IsDisposed)
      return;
    // Ask idle workers to flush. Busy workers flush on their own schedule.
    foreach (var worker in workers)
      worker.Writer.TryWrite(new WorkItem(new TaskCompletionSource()));

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
          var idle = options.Storage.PartitionIdleTimeout;
          var current = DateTime.UtcNow.Ticks;
          rawEvents?.Store.CloseIdle(idle, rawEvents.Store.Scheme.NameOf(current));
          foreach (var store in rollups.AllStores)
            store.CloseIdle(idle);
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

  void PersistSequence()
  {
    try
    {
      meta.Set("raw-sequence", Interlocked.Read(ref sequence).ToString(CultureInfo.InvariantCulture));
    }
    catch (ObjectDisposedException)
    {
    }
  }

  /// <summary>
  /// Stops accepting events, processes queued events, flushes and closes all stores.
  /// </summary>
  public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

  /// <inheritdoc cref="Dispose"/>
  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref disposed, 1) == 1)
      return;
    timer?.Dispose();
    foreach (var worker in workers)
      worker.Writer.TryComplete();
    try
    {
      await Task.WhenAll(workers.Select(x => x.Completion)).ConfigureAwait(false);
    }
    catch (Exception e)
    {
      logger.LogError(e);
    }
    lock (housekeepingLock)
    {
      PersistSequence();
      DisposeStorage();
    }
  }

  void DisposeStorage()
  {
    try
    {
      rawEvents?.Dispose();
      rollups?.Dispose();
      sessionsMaintainer?.Dispose();
      sessions?.Dispose();
    }
    finally
    {
      meta?.Dispose();
    }
  }
}
