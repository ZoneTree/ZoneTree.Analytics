using System.Globalization;
using System.Text;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Query;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Rebuilds the history of indexed joint definitions from retained raw events and their stored
/// session IDs, while ingestion runs.
/// </summary>
/// <remarks>
/// <para>
/// A definition added to an existing directory covers events from then on. Its reconstruction
/// reads the raw events stored before: those up to a fence, the raw event sequence when it began.
/// Ingestion writes every later event to the definition itself, late events included, so each
/// event counts once. A session that spans the fence adds up the same way: the reconstruction
/// contributes it as it was at the fence, ingestion the changes after it.
/// </para>
/// <para>
/// The contributions go to the rebuilt part of each row, which flushes keep as it is, so the
/// journal can write them again after a crash without undoing a flush. When the reconstruction
/// completes, the rollup index build folds them into the rows and then publishes the history:
/// until then, queries read raw events for it.
/// </para>
/// </remarks>
sealed class JointRollupReconstruction
{
  const string PendingKey = "rollup-index.backfill-pending";

  /// <summary>
  /// Completed reconstructions whose rebuilt parts the rollup index build folds in: lines of the
  /// coverage start and the definition's activation key.
  /// </summary>
  const string FoldingKey = "rollup-index.folding";

  const int BufferedRows = 4096;

  readonly AnalyticsOptions options;
  readonly RollupStore rollups;
  readonly RawEventStore rawEvents;
  readonly ReportingCalendar calendar;
  readonly SessionSchema schema;
  readonly MetaStore meta;
  readonly ILogger logger;
  readonly RollupDefinition[] definitions;
  readonly string signature;

  JointRollupReconstruction(AnalyticsOptions options, RollupStore rollups, RawEventStore rawEvents,
      ReportingCalendar calendar, SessionSchema schema, MetaStore meta, ILogger logger,
      RollupDefinition[] definitions, string signature, long coverage)
  {
    this.options = options;
    this.rollups = rollups;
    this.rawEvents = rawEvents;
    this.calendar = calendar;
    this.schema = schema;
    this.meta = meta;
    this.logger = logger;
    this.definitions = definitions;
    this.signature = signature;
    Coverage = coverage;
  }

  /// <summary>The start of the rebuilt history: raw retention must not pass it until the reconstruction completes.</summary>
  public long Coverage { get; }

  /// <summary>
  /// Decides which definitions need their history rebuilt, and prepares a new reconstruction
  /// before ingestion starts: it deletes the rows those definitions kept from an earlier
  /// activation. Returns null when there is nothing to rebuild. A new reconstruction reads the
  /// raw events up to <paramref name="fence"/>, the last sequence stored before ingestion starts.
  /// </summary>
  public static JointRollupReconstruction Prepare(AnalyticsOptions options, RollupStore rollups, RawEventStore rawEvents,
      RollupActivations activations, ReportingCalendar calendar, SessionSchema schema, MetaStore meta, ILogger logger, long fence)
  {
    using var check = new StageLog(logger, "Joint rollup backfill check", options.DataDirectory);
    if (rawEvents == null || options.Storage.RollupIndexes.Definitions.Count == 0)
    {
      check.Complete($"decision=disabled; reason={(rawEvents == null ? "raw event storage disabled" : "no configured combinations")}");
      return null;
    }
    // Only a rollup index build folds rebuilt history in and publishes it.
    var folding = Folding(meta).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    activations.Rebuilding(options.Rollups.Definitions
        .Where(definition => folding.Contains(RollupActivations.DefinitionKey(definition))).Select(definition => definition.Id));
    var partitions = rawEvents.Store.PartitionsInRange(0, DateTime.MaxValue.Ticks).ToArray();
    var cutoffs = RetentionCutoffs.Compute(options.Retention, calendar, DateTime.UtcNow.Ticks);
    var coverage = Math.Max(Math.Max(0, cutoffs.RawEvents), partitions.Length == 0 ? DateTime.UtcNow.Ticks : partitions[0].From);
    var pending = (meta.Get(PendingKey) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    var configured = options.Storage.RollupIndexes.Definitions.Select(index => index.Definition.Id).ToHashSet(StringComparer.Ordinal);
    var definitions = options.Rollups.Definitions.Where(definition => configured.Contains(definition.Id) &&
        !folding.Contains(RollupActivations.DefinitionKey(definition)) &&
        (pending.Contains(definition.Id) || activations.DefinitionSince(definition.Id) > coverage)).ToArray();
    var decision = definitions.Length == 0 ? "reuse existing rollups" : "rebuild required";
    var reason = definitions.Length == 0 ? "all definitions cover retained history" : "pending rebuild or definition activated after retained history";
    var pendingCount = definitions.Count(definition => pending.Contains(definition.Id));
    check.Complete($"decision={decision}; configured joint definitions={configured.Count}; definitions to rebuild={definitions.Length}; pending definitions={pendingCount}; raw partitions={partitions.Length}; coverage starts={new DateTime(coverage, DateTimeKind.Utc):O}; reason={reason}");
    if (definitions.Length == 0) return null;
    // Invalidate copies before changing their authoritative rows; the rollup index build folds
    // the rebuilt parts in and copies the rows again.
    var selected = definitions.Select(definition => definition.Id).ToHashSet(StringComparer.Ordinal);
    meta.Update(entries =>
    {
      entries["rollup-index.clean"] = "false";
      entries[PendingKey] = string.Join('\n', selected);
    });
    activations.Rebuilding(selected);
    // The raw partitions are part of the state, not the signature: ingestion adds partitions
    // while the reconstruction runs.
    var signature = "joint-backfill-v2\n" + string.Join('\n', definitions.OrderBy(definition => definition.Id, StringComparer.Ordinal)
        .Select(definition => definition.Id + ":" + (definition.Events == null ? "*" : string.Join(',', definition.Events.Order(StringComparer.Ordinal))))) + "\n" +
        options.Rollups.Granularities + "\n" +
        string.Join(',', options.Rollups.ValueAggregates.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + pair.Value)) + "\n" +
        string.Join(',', options.Rollups.DistinctDimensions.Order(StringComparer.Ordinal)) + "\n" +
        string.Join(',', schema.Names) + "\n" +
        FormattableString.Invariant($"{options.Rollups.HyperLogLogPrecision};{options.Sessions.Enabled};{options.Sessions.Timeout.Ticks};{options.Retention.RawEvents};{options.Retention.MinuteRollups};{options.Retention.HourRollups};{options.Retention.DayRollups}");
    // A checkpoint resumes only while its definitions are pending: one left behind by a completed
    // reconstruction belongs to an earlier activation of them.
    var checkpoint = new JointBackfillCheckpoint(options.DataDirectory, signature, () => new()
    {
      Coverage = coverage, Fence = fence, Partitions = [.. partitions.Select(partition => partition.Name)]
    }, resume: pending.SetEquals(selected));
    if (!checkpoint.State.Copying)
    {
      // The copies of deleted rows go too: the rollup index build starts over and drops them.
      meta.Update(entries =>
      {
        foreach (var key in entries.Keys.Where(key => key.StartsWith(RollupIndexStore.CheckpointPrefix + ".", StringComparison.Ordinal)).ToArray())
          entries.Remove(key);
      });
      // Ingestion writes to these rows from its start: rows of an earlier activation go first.
      DeleteDefinitions(rollups, selected);
      checkpoint.State.Copying = true;
      checkpoint.Save();
    }
    return new(options, rollups, rawEvents, calendar, schema, meta, logger, definitions, signature, checkpoint.State.Coverage);
  }

  /// <summary>
  /// Rebuilds the history from the journal's checkpoint, on the background maintenance thread.
  /// Every attempt resumes from the journal, so a failed attempt can simply be repeated.
  /// </summary>
  public void Run(CancellationToken cancellationToken)
  {
    using var build = new StageLog(logger, "Joint rollup backfill", options.DataDirectory);
    var checkpoint = new JointBackfillCheckpoint(options.DataDirectory, signature,
        () => throw new InvalidOperationException("The joint rollup backfill journal is missing."));
    var state = checkpoint.State;
    var coverage = state.Coverage;
    var fence = state.Fence;
    checkpoint.Replay(rollups);
    var selected = definitions.Select(definition => definition.Id).ToHashSet(StringComparer.Ordinal);
    var cutoffs = RetentionCutoffs.Compute(options.Retention, calendar, DateTime.UtcNow.Ticks);
    var buffer = new RollupBuffer(options.Rollups.Definitions.ToArray(), options.Rollups.Granularities, calendar,
        options.Rollups.HyperLogLogPrecision, schema, options.Rollups.ValueAggregates, options.Rollups.DistinctDimensions, selected)
        { Cutoffs = cutoffs };
    var names = state.Partitions.ToHashSet(StringComparer.Ordinal);
    var partitions = rawEvents.Store.PartitionsInRange(0, DateTime.MaxValue.Ticks).Where(partition => names.Contains(partition.Name)).ToArray();
    var record = new RawEvent();
    var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
    var changes = new List<ValueChange>();
    var collector = new RawSessionReader(rawEvents, schema, options);
    var timeout = options.Sessions.Timeout.Ticks;
    var resumed = state.Events;
    var resumeSite = state.Site;
    var resumeSiteCompleted = state.SiteCompleted;
    var resumePartition = state.Partition;
    var resumeKey = state.Key;
    void Flush(bool report = false)
    {
      if (buffer.Count > 0)
      {
        state.Redo = rollups.RebuiltParts(buffer.Swap());
        state.Updates += state.Redo.Count;
        state.Flushes++;
      }
      // Cursor/session state and rebuilt parts become durable before any of them is written.
      checkpoint.Save();
      if (report && state.Redo.Count > 0)
        build.Progress($"checkpoint prepared; events={state.Events}; resumed events={resumed}; replacement rows={state.Redo.Count}", state.Events - resumed, "events");
      checkpoint.Replay(rollups);
      if (report) build.Progress($"checkpoint saved; events={state.Events}; resumed events={resumed}; rollup row updates={state.Updates}; flushes={state.Flushes}; sessions={state.Sessions}", state.Events - resumed, "events");
      cancellationToken.ThrowIfCancellationRequested();
    }
    void Finish(string site, RawSession session)
    {
      state.Sessions++;
      if (session.Start >= coverage)
      {
        RawSessionContributions.SessionValues(session, changes);
        buffer.Contribute(site, StandardEvents.Session, session.Start, session.Dimensions, 1, session.Visitor, changes, DimensionScope.Session);
      }
      if (RawSessionContributions.Entrance(session, out var ticks, out var page, out var hostname))
        ContributePageMark(site, session, ticks, page, hostname, StandardValues.Entrances);
      if (RawSessionContributions.Exit(session, out ticks, out page, out hostname))
        ContributePageMark(site, session, ticks, page, hostname, StandardValues.Exits);
    }
    void ContributePageMark(string site, RawSession session, long ticks, string page, string hostname, string value)
    {
      if (ticks < coverage)
        return;
      changes.Clear();
      changes.Add(new(value, 1, true));
      buffer.ContributePageMark(site, ticks, session.Dimensions, page, hostname, changes);
    }
    // One chronological stream per site keeps only sessions within an inactivity window.
    // Original session IDs/starts preserve attribution; events are never re-ingested or re-enriched.
    foreach (var site in Sites(rawEvents, partitions))
    {
      var comparison = string.CompareOrdinal(site, resumeSite);
      if (comparison < 0 || (comparison == 0 && resumeSiteCompleted)) continue;
      var continuing = comparison == 0;
      var active = continuing ? state.Active : new Dictionary<ulong, RawSession>();
      if (!continuing) state.Sites++;
      state.Site = site;
      state.SiteCompleted = false;
      state.Active = active;
      var queue = new PriorityQueue<(ulong Id, long Last), long>();
      foreach (var (id, session) in active) queue.Enqueue((id, session.Last), session.Last);
      var prefix = KeyCodec.SitePrefix(site);
      foreach (var partition in partitions)
      {
        if (continuing && string.CompareOrdinal(partition.Name, resumePartition) < 0) continue;
        using var lease = rawEvents.Store.Acquire(partition.Name);
        if (lease == null) continue;
        using var iterator = lease.Tree.CreateIterator(new IteratorOptions { DiskSegmentPrefetchSize = 64, ContributeToTheBlockCache = true });
        var after = continuing && partition.Name == resumePartition ? resumeKey : null;
        iterator.Seek(after is { Length: > 0 } ? after : KeyCodec.SeekKey(prefix, Math.Max(coverage, partition.From)));
        while (iterator.Next())
        {
          var key = iterator.CurrentKey.Span;
          if (!key.StartsWith(prefix)) break;
          if (after is { Length: > 0 } && key.SequenceCompareTo(after) <= 0) continue;
          // Ingestion has written the events after the fence to every definition.
          if (KeyCodec.ReadRawSequence(key) > fence) continue;
          var ticks = KeyCodec.ReadRawTicks(key);
          if (!RawEventCodec.TryDecode(iterator.CurrentValue.Span, ticks, null, default, record)) continue;
          while (queue.TryPeek(out var old, out var last) && last < ticks - Math.Min(ticks, timeout))
          {
            queue.Dequeue();
            if (!active.TryGetValue(old.Id, out var session) || session.Last != old.Last) continue;
            Finish(site, session); active.Remove(old.Id);
            // The source cursor still points to the preceding event. Save finished
            // sessions before processing this event, preserving the remaining active set.
            if (buffer.Count >= BufferedRows) Flush(true);
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
          state.Partition = partition.Name;
          state.Key = iterator.CurrentKey.ToArray();
          ++state.Events;
          if (buffer.Count >= BufferedRows || state.Events % IndexBackfillCheckpoint.Interval == 0) Flush(true);
        }
        Flush(true);
      }
      foreach (var id in active.Keys.ToArray())
      {
        Finish(site, active[id]);
        active.Remove(id);
        if (buffer.Count >= BufferedRows) Flush(true);
      }
      state.SiteCompleted = true;
      state.Partition = null;
      state.Key = null;
      Flush(true);
    }
    Flush();
    // The journal goes first: if the process dies before the definitions wait for their fold,
    // they are still pending and their reconstruction starts over.
    checkpoint.Complete();
    meta.Update(entries =>
    {
      var folding = entries.GetValueOrDefault(FoldingKey) ?? "";
      entries[FoldingKey] = folding + string.Concat(definitions.Select(definition =>
          coverage.ToString(CultureInfo.InvariantCulture) + "\t" + RollupActivations.DefinitionKey(definition) + "\n"));
      entries[PendingKey] = "";
    });
    build.Complete($"definitions={definitions.Length}; raw partitions={partitions.Length}; sites={state.Sites}; events={state.Events}; resumed events={resumed}; sessions={state.Sessions}; rollup row updates={state.Updates}; flushes={state.Flushes}", state.Events - resumed, "events");
  }

  /// <summary>Whether a reconstruction has not completed: the rollup index build folds its rows in after it.</summary>
  public static bool IsPending(MetaStore meta) => !string.IsNullOrEmpty(meta.Get(PendingKey));

  /// <summary>Whether a completed reconstruction waits for the rollup index build to fold its rows in.</summary>
  public static bool HasFolding(MetaStore meta) => Folding(meta).Any();

  /// <summary>
  /// The configured definitions whose rows wait to be folded in, indexed or not: a definition
  /// can keep its rollups after its index is removed.
  /// </summary>
  public static HashSet<string> FoldingDefinitions(MetaStore meta, IEnumerable<RollupDefinition> configured)
  {
    var keys = Folding(meta).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    return configured.Where(definition => keys.Contains(RollupActivations.DefinitionKey(definition)))
        .Select(definition => definition.Id).ToHashSet(StringComparer.Ordinal);
  }

  /// <summary>
  /// Publishes the history of the reconstructions whose rows the rollup index build folded in.
  /// A definition whose configuration changed since is left out: its next reconstruction starts over.
  /// </summary>
  public static void PublishFolded(MetaStore meta, RollupActivations activations, IEnumerable<RollupDefinition> configured)
  {
    var folded = Folding(meta).ToArray();
    if (folded.Length == 0) return;
    var byKey = configured.ToDictionary(RollupActivations.DefinitionKey, StringComparer.Ordinal);
    foreach (var group in folded.GroupBy(entry => entry.Since))
      activations.Backfilled(meta, group.Where(entry => byKey.ContainsKey(entry.Key)).Select(entry => byKey[entry.Key]), group.Key);
    meta.Set(FoldingKey, "");
  }

  static IEnumerable<(long Since, string Key)> Folding(MetaStore meta)
  {
    foreach (var line in (meta.Get(FoldingKey) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
      var separator = line.IndexOf('\t', StringComparison.Ordinal);
      yield return (long.Parse(line.AsSpan(0, separator), CultureInfo.InvariantCulture), line[(separator + 1)..]);
    }
  }

  static SortedSet<string> Sites(RawEventStore rawEvents, IEnumerable<PartitionRange> partitions)
  {
    var sites = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var partition in partitions)
    {
      using var lease = rawEvents.Store.Acquire(partition.Name);
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

  /// <summary>
  /// Deletes every row of the definitions. Rows are ordered by site, event and definition, so
  /// it seeks from one site and event to the next instead of reading the other definitions.
  /// </summary>
  static void DeleteDefinitions(RollupStore rollups, HashSet<string> definitions)
  {
    var ids = definitions.Select(Encoding.UTF8.GetBytes).Order(Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))).ToArray();
    foreach (var store in rollups.PrimaryStores)
      foreach (var partition in store.ListPartitions())
      {
        using var lease = store.Acquire(partition);
        if (lease == null) continue;
        var deleted = false;
        using var iterator = lease.Tree.CreateIterator(new IteratorOptions { IteratorType = IteratorType.NoRefresh });
        iterator.SeekFirst();
        while (iterator.Next())
        {
          var key = iterator.CurrentKey.Span;
          var head = KeyCodec.HeadLength(key, 2);
          var id = key[head..(head + key[head..].IndexOf(KeyCodec.Terminator))];
          var next = 0;
          while (next < ids.Length && ids[next].AsSpan().SequenceCompareTo(id) < 0)
            ++next;
          if (next < ids.Length && ids[next].AsSpan().SequenceEqual(id))
          {
            lease.Tree.ForceDelete(iterator.CurrentKey);
            deleted = true;
            continue;
          }
          // The next definition to delete of this site and event, or the next site and event.
          Memory<byte> seek = next < ids.Length ? (byte[])[.. key[..head], .. ids[next], KeyCodec.Terminator]
              : KeyCodec.AfterSegment(key[..(head - 1)]);
          iterator.Seek(seek);
        }
        // The journal records that the rows are gone once this returns.
        if (deleted)
          lease.Persist();
      }
  }
}
