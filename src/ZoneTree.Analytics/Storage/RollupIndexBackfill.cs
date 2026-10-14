using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Query;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>Rebuilds only indexed joint definitions, using enriched retained events and their stored session IDs.</summary>
static class RollupIndexBackfill
{
  const string PendingKey = "rollup-index.backfill-pending";

  public static bool Build(AnalyticsOptions options, RollupStore rollups, RawEventStore rawEvents,
      RollupActivations activations, ReportingCalendar calendar, SessionSchema schema, MetaStore meta, ILogger logger)
  {
    using var check = new StartupLog(logger, "Joint rollup backfill check", options.DataDirectory);
    if (rawEvents == null || options.Storage.RollupIndexes.Definitions.Count == 0)
    {
      check.Complete($"decision=disabled; reason={(rawEvents == null ? "raw event storage disabled" : "no configured combinations")}");
      return false;
    }
    var partitions = rawEvents.Store.PartitionsInRange(0, DateTime.MaxValue.Ticks).ToArray();
    var cutoffs = RetentionCutoffs.Compute(options.Retention, calendar, DateTime.UtcNow.Ticks);
    var coverage = Math.Max(Math.Max(0, cutoffs.RawEvents), partitions.Length == 0 ? DateTime.UtcNow.Ticks : partitions[0].From);
    var pending = (meta.Get(PendingKey) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    var configured = options.Storage.RollupIndexes.Definitions.Select(index => index.Definition.Id).ToHashSet(StringComparer.Ordinal);
    var definitions = options.Rollups.Definitions.Where(definition => configured.Contains(definition.Id) &&
        (pending.Contains(definition.Id) || activations.DefinitionSince(definition.Id) > coverage)).ToArray();
    var decision = definitions.Length == 0 ? "reuse existing rollups" : "rebuild required";
    var reason = definitions.Length == 0 ? "all definitions cover retained history" : "pending rebuild or definition activated after retained history";
    var pendingCount = definitions.Count(definition => pending.Contains(definition.Id));
    check.Complete($"decision={decision}; configured joint definitions={configured.Count}; definitions to rebuild={definitions.Length}; pending definitions={pendingCount}; raw partitions={partitions.Length}; coverage starts={new DateTime(coverage, DateTimeKind.Utc):O}; reason={reason}");
    if (definitions.Length == 0) return false;
    using var build = new StartupLog(logger, "Joint rollup backfill", options.DataDirectory);
    var selected = definitions.Select(definition => definition.Id).ToHashSet(StringComparer.Ordinal);
    meta.Set(PendingKey, string.Join('\n', selected));
    DeleteDefinitions(rollups, selected);
    var buffer = new RollupBuffer(options.Rollups.Definitions.ToArray(), options.Rollups.Granularities, calendar,
        options.Rollups.HyperLogLogPrecision, schema, options.Rollups.ValueAggregates, options.Rollups.DistinctDimensions, selected)
        { Cutoffs = cutoffs };
    var record = new RawEvent();
    var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
    var changes = new List<ValueChange>();
    var pageDimensions = new PageDimensions();
    var collector = new RawSessionReader(rawEvents, schema, options);
    var timeout = options.Sessions.Timeout.Ticks;
    long count = 0;
    long updates = 0;
    long flushes = 0;
    long sessions = 0;
    var sites = 0;
    void Flush()
    {
      if (buffer.Count == 0) return;
      using var batch = new RollupWriteBatch(buffer.Swap());
      var rows = batch.Rows.Count;
      rollups.Write(batch);
      updates += rows;
      flushes++;
    }
    void Finish(string site, RawSession session)
    {
      sessions++;
      if (session.Start >= coverage)
      {
        changes.Clear();
        changes.Add(new(StandardValues.Bounces, session.Interactions <= 1 ? 1 : 0, true));
        changes.Add(new(StandardValues.Duration, Math.Max(0, session.Last - session.Start) / (double)TimeSpan.TicksPerSecond, true));
        changes.Add(new(StandardValues.SessionPageViews, session.PageViews, true));
        buffer.Contribute(site, StandardEvents.Session, session.Start, session.Dimensions, 1, session.Visitor, changes, DimensionScope.Session);
      }
      if (session.FirstIsPageView && session.FirstPage != null && session.Start >= coverage)
      {
        changes.Clear(); changes.Add(new(StandardValues.Entrances, 1, true));
        buffer.Contribute(site, StandardEvents.PageView, session.Start,
            pageDimensions.Of(session.Dimensions, session.FirstPage, session.FirstHostname), 0, 0, changes, DimensionScope.PageView);
      }
      if (session.LastPage != null && session.LastPageTicks >= coverage)
      {
        changes.Clear(); changes.Add(new(StandardValues.Exits, 1, true));
        buffer.Contribute(site, StandardEvents.PageView, session.LastPageTicks,
            pageDimensions.Of(session.Dimensions, session.LastPage, session.LastHostname), 0, 0, changes, DimensionScope.PageView);
      }
      if (buffer.Count >= 4096) Flush();
    }
    // One chronological stream per site keeps only sessions within an inactivity window.
    // Original session IDs/starts preserve attribution; events are never re-ingested or re-enriched.
    foreach (var site in Sites(rawEvents))
    {
      sites++;
      var active = new Dictionary<ulong, RawSession>();
      var queue = new PriorityQueue<(ulong Id, long Last), long>();
      var prefix = KeyCodec.SitePrefix(site);
      foreach (var partition in partitions)
      {
        using var lease = rawEvents.Store.Acquire(partition.Name);
        if (lease == null) continue;
        using var iterator = lease.Tree.CreateIterator(new IteratorOptions { DiskSegmentPrefetchSize = 64, ContributeToTheBlockCache = true });
        iterator.Seek(KeyCodec.SeekKey(prefix, Math.Max(coverage, partition.From)));
        while (iterator.Next())
        {
          var key = iterator.CurrentKey.Span;
          if (!key.StartsWith(prefix)) break;
          var ticks = KeyCodec.ReadRawTicks(key);
          if (!RawEventCodec.TryDecode(iterator.CurrentValue.Span, ticks, null, default, record)) continue;
          while (queue.TryPeek(out var old, out var last) && last < ticks - Math.Min(ticks, timeout))
          {
            queue.Dequeue();
            if (!active.TryGetValue(old.Id, out var session) || session.Last != old.Last) continue;
            Finish(site, session); active.Remove(old.Id);
          }
          dimensions.Clear();
          foreach (var pair in record.Dimensions) dimensions[pair.Key] = pair.Value;
          changes.Clear();
          foreach (var pair in record.Values) changes.Add(new(pair.Key, pair.Value, false));
          buffer.Contribute(site, record.Name, ticks, dimensions, 1, record.Visitor, changes,
              record.Name == StandardEvents.Bot ? DimensionScope.Bot : DimensionScope.All);
          if (options.Sessions.Enabled && record.SessionId != 0)
          {
            collector.Collect(active, record, true);
            var session = active[record.SessionId];
            queue.Enqueue((record.SessionId, session.Last), session.Last);
          }
          if (buffer.Count >= 4096) Flush();
          if (++count % 100_000 == 0) build.Progress($"events={count}; rollup row updates={updates}; flushes={flushes}; sessions={sessions}", count, "events");
        }
      }
      foreach (var session in active.Values) Finish(site, session);
      Flush();
    }
    Flush();
    // Persist replacements and drain WALs before publishing historical coverage. A crash before
    // this checkpoint leaves the pending marker, so startup clears and repeats the partial build.
    foreach (var store in rollups.PrimaryStores)
    {
      store.Compact();
      store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    }
    activations.Backfilled(meta, definitions, coverage);
    meta.Set(PendingKey, "");
    build.Complete($"definitions={definitions.Length}; raw partitions={partitions.Length}; sites={sites}; events={count}; sessions={sessions}; rollup row updates={updates}; flushes={flushes}", count, "events");
    return true;
  }

  static SortedSet<string> Sites(RawEventStore rawEvents)
  {
    var sites = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var partition in rawEvents.Store.ListPartitions())
    {
      using var lease = rawEvents.Store.Acquire(partition);
      if (lease == null) continue;
      using var iterator = lease.Tree.CreateIterator();
      iterator.SeekFirst();
      while (iterator.Next())
      {
        var key = iterator.CurrentKey.Span;
        var site = KeyCodec.ReadSegment(key, 0, out var head);
        sites.Add(site);
        iterator.Seek(KeyCodec.AfterSegment(key[..(head - 1)]));
      }
    }
    return sites;
  }

  static void DeleteDefinitions(RollupStore rollups, HashSet<string> definitions)
  {
    foreach (var store in rollups.PrimaryStores)
      foreach (var partition in store.ListPartitions())
      {
        using var lease = store.Acquire(partition);
        if (lease == null) continue;
        using var iterator = lease.Tree.CreateIterator(new IteratorOptions { IteratorType = IteratorType.NoRefresh });
        iterator.SeekFirst();
        while (iterator.Next())
          if (definitions.Contains(StorageKeyCodec.DecodeRollup(iterator.CurrentKey.Span).DimensionSetId))
            lease.Tree.ForceDelete(iterator.CurrentKey);
      }
  }
}
