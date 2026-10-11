using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>
/// Plans and executes queries over rollups and raw events.
/// </summary>
/// <remarks>
/// <para>
/// The execution flow is preparation, read planning, aggregation, then result construction.
/// <see cref="QueryPreparation"/> validates the request, <see cref="QueryRangePlanner"/>
/// covers its range, <see cref="RawSessionReader"/> reconstructs sessions, and
/// <see cref="QueryResultBuilder"/> evaluates and formats the aggregated metrics.
/// </para>
/// <para>
/// A query reads one or more events (the sources its metrics need). Each source is planned
/// separately: its dimension set (group-by plus filter dimensions) selects a rollup definition,
/// and the time range is covered greedily from coarse to fine: whole days from daily rollups,
/// remaining whole hours from hourly rollups, remaining minutes from minute rollups, and any
/// remainder from raw events. Each level is used only where it is enabled, retained, already
/// active, and compatible with the query's time zone.
/// </para>
/// <para>
/// Sources are aggregated into groups keyed by time bucket and group values, then joined to
/// compute the metrics. Raw parts of several sources over the same range share one scan.
/// Behavioral segments are evaluated with an extra raw pass and force raw execution.
/// </para>
/// </remarks>
sealed class QueryEngine
{
  const int CancellationCheckInterval = RawEventStore.CancellationCheckInterval;

  static readonly Granularity[] CoarseToFine = [Granularity.Day, Granularity.Hour, Granularity.Minute];

  readonly AnalyticsOptions options;

  readonly ReportingCalendar storageCalendar;

  readonly RollupStore rollups;

  readonly RawEventStore rawEvents;

  readonly RollupActivations activations;

  readonly EnrichmentPipeline enricher;

  readonly RawSessionReader sessionReader;

  readonly QueryPreparation preparation;

  public QueryEngine(
      AnalyticsOptions options,
      ReportingCalendar storageCalendar,
      RollupStore rollups,
      RawEventStore rawEvents,
      RollupDefinition[] definitions,
      RollupActivations activations,
      EnrichmentPipeline enricher)
  {
    this.options = options;
    this.storageCalendar = storageCalendar;
    this.rollups = rollups;
    this.rawEvents = rawEvents;
    this.activations = activations;
    this.enricher = enricher;
    sessionReader = new(rawEvents, enricher.Schema, options);
    preparation = new(options, storageCalendar, definitions, activations, enricher);
  }

  #region Entry points

  public AnalyticsResult Execute(AnalyticsQuery query, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(query);
    var now = DateTime.UtcNow.Ticks;
    var compare = query.Compare != null;
    var (result, bucketKeys) = ExecuteCore(query, now, compare, cancellationToken);
    if (!compare)
      return result;

    var calendar = preparation.CalendarFor(query.TimeZone);
    var previousRange = query.Compare.Resolve(query.Range, calendar, now);
    var (previous, previousBucketKeys) = ExecuteCore(query.CloneForRange(previousRange), now, true, cancellationToken);
    QueryResultBuilder.AttachComparison(result, bucketKeys, previous, previousBucketKeys);
    return result;
  }

  (AnalyticsResult Result, Dictionary<AnalyticsRow, int> BucketIndexes) ExecuteCore(
      AnalyticsQuery query, long now, bool includeBucketIndexes, CancellationToken cancellationToken)
  {
    var context = preparation.Prepare(query, now, cancellationToken);
    if (context.Segments.Length > 0)
    {
      if (rawEvents == null)
        throw new AnalyticsQueryException("Segments require raw events (StorageOptions.StoreRawEvents).");
      AddWarning(context, context.Cutoffs.RawEventsWarning(context.From));
      context.SegmentSets = SegmentSets.Evaluate(
          rawEvents, context.Site, context.From, context.To, context.Segments,
          options.Queries.MaxDegreeOfParallelism, context.CancellationToken);
    }
    foreach (var source in context.Sources.Values)
      PlanReads(context, source, query.Source);
    ReadSources(context);
    return QueryResultBuilder.Build(context, query, includeBucketIndexes);
  }

  #endregion

  #region Planning

  void PlanReads(QueryContext context, SourceContext source, QuerySource selection)
  {
    var readRaw = selection != QuerySource.Rollups && rawEvents != null;
    var tiers = source.Definition != null && selection != QuerySource.RawEvents && source.MissingAggregates == null
        ? AvailableRollups(context.Calendar, context.Interval, context.Cutoffs,
            Math.Max(activations.DefinitionSince(source.Definition.Id), source.AggregatesSince))
        : [];
    if (context.Segments.Length > 0)
    {
      if (!readRaw)
        throw new AnalyticsQueryException("Segments require raw events, which are not available for this query.");
      tiers.Clear();
    }
    if (tiers.Count == 0 && !readRaw)
    {
      if (selection == QuerySource.RawEvents)
        throw new AnalyticsQueryException("Raw events are not stored (StorageOptions.StoreRawEvents is false).");
      if (source.MissingAggregates != null)
        throw new AnalyticsQueryException(
            $"Rollups do not keep the {source.MissingAggregates} for the event '{source.Event}', and raw events are " +
            "not available. Add them to Rollups.ValueAggregates or Rollups.DistinctDimensions.");
      if (source.Definition == null)
      {
        var dimensions = context.GroupBy
            .Concat(context.Filters.Select(x => x.Dimension))
            .Concat(source.Conditions.Select(x => x.Dimension))
            .Distinct();
        var conditions = source.Conditions.Length == 0 ? "" : $" where {string.Join(" and ", source.Conditions.Select(x => x.ToString()))}";
        throw new AnalyticsQueryException(
            $"No rollup covers the dimensions [{string.Join(", ", dimensions)}] for the event '{source.Event}'{conditions}, " +
            "and raw events are not available. Add a RollupDefinition with exactly these dimensions.");
      }
      throw new AnalyticsQueryException(
          $"No rollup granularity can answer the {context.Interval} interval. Enable a finer granularity.");
    }

    var plan = QueryRangePlanner.Build(context.Calendar, context.From, context.To, tiers, readRaw,
        context.CancellationToken, context.Interval);
    source.Plan = plan.Segments;
    foreach (var segment in plan.Segments.Where(x => x.Source == PlanSource.RawEvents))
      AddWarning(context, context.Cutoffs.RawEventsWarning(segment.From));
    foreach (var granularity in CoarseToFine)
    {
      if ((plan.WholeBucketSources & granularity) == 0)
        continue;
      context.Warnings.Add(
          $"Parts of the range of '{source.Event}' were answered with whole {granularity.ToString().ToLowerInvariant()} buckets " +
          "because finer data is not available.");
    }
    if (plan.HasMissingRanges)
      context.Warnings.Add($"Part of the range of '{source.Event}' could not be answered.");
  }

  List<RollupCoverage> AvailableRollups(
      ReportingCalendar calendar, TimeInterval interval, RetentionCutoffs cutoffs, long requiredSince = 0)
  {
    var tiers = new List<RollupCoverage>(3);
    foreach (var granularity in CoarseToFine)
    {
      if (!rollups.IsEnabled(granularity))
        continue;
      if (granularity == Granularity.Day &&
          (calendar.TimeZone.Id != storageCalendar.TimeZone.Id || interval.IsFixedWidth))
        continue;
      if (granularity == Granularity.Hour && interval.Unit == TimeIntervalUnit.Minute)
        continue;
      var availableFrom = Math.Max(requiredSince,
          Math.Max(cutoffs.Of(granularity), activations.GranularitySince(granularity)));
      tiers.Add(new(granularity, availableFrom));
    }
    return tiers;
  }

  static void AddWarning(QueryContext context, string warning)
  {
    if (warning != null)
      context.Warnings.Add(warning);
  }

  #endregion

  #region Execution

  void ReadSources(QueryContext context)
  {
    // Raw parts of several sources over the same range share one scan.
    var rawBatches = new Dictionary<(long, long), List<SourceContext>>();
    foreach (var source in context.Sources.Values)
    {
      foreach (var segment in source.Plan)
      {
        context.Steps.Add(new QueryPlanStep(
            source.Event,
            segment.Source,
            new DateTimeOffset(segment.From, TimeSpan.Zero),
            new DateTimeOffset(segment.To, TimeSpan.Zero)));
        if (segment.Source == PlanSource.RawEvents)
        {
          if (!rawBatches.TryGetValue((segment.From, segment.To), out var batch))
            rawBatches[(segment.From, segment.To)] = batch = [];
          batch.Add(source);
        }
        else
        {
          ReadRollups(context, source, segment);
        }
      }
    }
    foreach (var ((from, to), batch) in rawBatches)
      ReadRawEvents(context, new ReadSegment(PlanSource.RawEvents, from, to), batch);
  }

  void ReadRollups(QueryContext context, SourceContext source, ReadSegment segment)
  {
    var definition = source.Definition;
    var dimensionCount = definition.Dimensions.Count;
    var calendar = context.Calendar;
    var granularity = segment.Granularity;
    var (fromBucket, toBucket) = segment.Buckets(calendar);
    var isDay = segment.Source == PlanSource.DayRollups;
    var interval = context.Interval;
    var groupValues = new string[context.GroupBy.Length];
    var counter = 0;
    var requirements = source.ReadRequirements;
    rollups.Scan(granularity, context.Site, source.Event, definition.Id, fromBucket, toBucket,
        (bucket, combined, value) =>
        {
          if (++counter % CancellationCheckInterval == 0)
            context.CancellationToken.ThrowIfCancellationRequested();
          var parts = KeyCodec.SplitValues(combined, dimensionCount);
          foreach (var binding in source.RollupFilters)
          {
            if (!binding.Filter.IsMatch(KeyCodec.Public(parts[binding.DimensionIndex])))
              return;
          }
          long outputBucket;
          if (interval == TimeInterval.None)
            outputBucket = 0;
          else if (isDay)
            outputBucket = calendar.LocalBucket(bucket, interval);
          else
            outputBucket = calendar.Bucket(bucket, interval);
          for (var i = 0; i < groupValues.Length; ++i)
            groupValues[i] = parts[source.GroupByIndexes[i]];
          GetGroup(source.Groups, outputBucket, groupValues).MergeForQuery(value, requirements);
        });
  }

  /// <summary>
  /// Returns the group of stored-form values (missing values as <see cref="KeyCodec.MissingValue"/>).
  /// </summary>
  static RollupAccumulator GetGroup(Dictionary<GroupKey, RollupAccumulator> groups, long bucket, string[] storedValues)
  {
    var combined = storedValues.Length switch
    {
      0 => "",
      1 => storedValues[0],
      _ => string.Join(KeyCodec.ValueSeparator, storedValues)
    };
    var key = new GroupKey(bucket, combined);
    if (!groups.TryGetValue(key, out var accumulator))
    {
      accumulator = new RollupAccumulator();
      groups.Add(key, accumulator);
    }
    return accumulator;
  }

  /// <summary>
  /// Moves groups into the target. The source must not be used afterwards.
  /// </summary>
  static void MergeGroups(Dictionary<GroupKey, RollupAccumulator> target, Dictionary<GroupKey, RollupAccumulator> source)
  {
    foreach (var (key, accumulator) in source)
    {
      if (target.TryGetValue(key, out var existing))
        existing.Merge(accumulator);
      else
        target.Add(key, accumulator);
    }
  }

  /// <summary>
  /// The partial results of one slice of a raw scan.
  /// </summary>
  sealed class RawSlice(int eventSourceCount, int sessionShardCount, int groupByCount) : RawScanSlice(sessionShardCount)
  {
    /// <summary>The groups of each event source.</summary>
    public readonly Dictionary<GroupKey, RollupAccumulator>[] Groups =
        Shards.Create<Dictionary<GroupKey, RollupAccumulator>>(eventSourceCount);

    public readonly string[] GroupValues = new string[groupByCount];
  }

  void ReadRawEvents(QueryContext context, ReadSegment segment, List<SourceContext> sources)
  {
    var eventSources = sources.Where(x => x.Event != StandardEvents.Session).ToArray();
    // Sessions, and entrances and exits when a metric reads them, are replayed from the events
    // of each session. Like their rollups, they carry session-level and page-view-level
    // dimensions only.
    var flowSources = sources.Where(x => x.Event == StandardEvents.Session || x.ReadsPageFlow).ToArray();
    var trackSessions = options.Sessions.Enabled && flowSources.Length > 0;
    var onlyEvent = eventSources.Length == 1 && !trackSessions ? eventSources[0] : null;

    var needed = context.GroupBy.Concat(sources.SelectMany(x => x.Filters).Select(x => x.Dimension))
        .Concat(sources.SelectMany(x => x.DistinctDimensions));
    if (trackSessions)
      needed = needed.Append(StandardDimensions.Page).Append(StandardDimensions.Hostname);
    var selector = new DimensionSelector(needed);

    var segmentSets = context.SegmentSets;
    var maxDegree = options.Queries.MaxDegreeOfParallelism;
    var shardCount = trackSessions ? maxDegree : 0;

    var slices = rawEvents.Scan(
        context.Site, segment.From, segment.To, maxDegree,
        () => new RawSlice(eventSources.Length, shardCount, context.GroupBy.Length),
        (slice, ticks, value) =>
        {
          var rawEvent = slice.RawEvent;
          var decoded = onlyEvent != null
              ? RawEventCodec.TryDecode(value, ticks, onlyEvent.Event, onlyEvent.EventUtf8, rawEvent, selector)
              : RawEventCodec.TryDecode(value, ticks, null, default, rawEvent, selector);
          if (!decoded)
            return;
          var allowed = segmentSets == null || segmentSets.Allows(rawEvent.Visitor, rawEvent.SessionId);
          if (allowed)
          {
            for (var i = 0; i < eventSources.Length; ++i)
            {
              if (eventSources[i].Event == rawEvent.Name)
                AddRawEvent(context, eventSources[i], slice.Groups[i], rawEvent, slice.GroupValues);
            }
          }
          if (trackSessions && rawEvent.SessionId != 0)
            sessionReader.Collect(slice.Sessions[Shards.Of(rawEvent.SessionId, shardCount)], rawEvent, allowed);
        },
        context.CancellationToken);

    ParallelWork.For(eventSources.Length, maxDegree, i =>
    {
      foreach (var slice in slices)
        MergeGroups(eventSources[i].Groups, slice.Groups[i]);
    });
    if (!trackSessions)
      return;

    AggregateSessions(context, segment, flowSources, slices, selector);
  }

  void AggregateSessions(
      QueryContext context, ReadSegment segment, SourceContext[] flowSources, RawSlice[] slices, DimensionSelector selector)
  {
    var maxDegree = options.Queries.MaxDegreeOfParallelism;
    var joined = sessionReader.Join(slices);
    var precedingFrom = flowSources.Any(x => x.ReadsSessionActivity)
        ? sessionReader.ReadPreceding(context.Site, segment.From, segment.To, selector, joined, context.CancellationToken)
        : null;
    if (precedingFrom.HasValue)
      AddWarning(context, context.Cutoffs.RawEventsWarning(precedingFrom.Value));
    sessionReader.ReadContinuation(context.Site, segment.To, context.NowTicks, selector, joined, context.CancellationToken);

    // A session can span slices. Its parts are joined in time order, shard by shard, and each
    // shard adds its complete sessions to groups of its own, per sub-query.
    var flowGroups = new Dictionary<GroupKey, RollupAccumulator>[joined.Length][];
    ParallelWork.For(joined.Length, maxDegree, shard =>
    {
      var groupValues = new string[context.GroupBy.Length];
      var groups = Shards.Create<Dictionary<GroupKey, RollupAccumulator>>(flowSources.Length);
      var pageDimensions = flowSources.Any(x => x.ReadsPageFlow) ? new PageDimensions() : null;
      foreach (var session in joined[shard].Values)
      {
        if (session.Dimensions == null || !session.Allowed)
          continue;
        for (var i = 0; i < flowSources.Length; ++i)
        {
          var source = flowSources[i];
          if (source.Event == StandardEvents.Session)
          {
            if (session.Start >= segment.From && session.Start < segment.To)
              AddRawSession(context, source, groups[i], session, groupValues);
          }
          else
          {
            AddRawEntranceAndExit(context, source, groups[i], session, groupValues, segment, pageDimensions);
          }
        }
      }
      flowGroups[shard] = groups;
    });
    for (var i = 0; i < flowSources.Length; ++i)
    {
      for (var shard = 0; shard < joined.Length; ++shard)
        MergeGroups(flowSources[i].Groups, flowGroups[shard][i]);
    }
  }

  static void AddRawEvent(
      QueryContext context,
      SourceContext source,
      Dictionary<GroupKey, RollupAccumulator> groups,
      RawEvent rawEvent,
      string[] groupValues)
  {
    foreach (var filter in source.Filters)
    {
      rawEvent.TryGetDimension(filter.Dimension, out var value);
      if (!filter.IsMatch(value))
        return;
    }
    for (var i = 0; i < groupValues.Length; ++i)
    {
      rawEvent.TryGetDimension(context.GroupBy[i], out var value);
      groupValues[i] = KeyCodec.Stored(value);
    }
    var accumulator = GetGroup(groups, context.Calendar.Bucket(rawEvent.Ticks, context.Interval), groupValues);
    ++accumulator.Count;
    if (source.ReadsVisitors)
      accumulator.AddVisitor(rawEvent.Visitor, context.Precision);
    foreach (var (name, value) in rawEvent.Values)
    {
      accumulator.AddValue(name, value);
      if (source.ValueDetails != null && source.ValueDetails.TryGetValue(name, out var aggregates))
      {
        var bucket = (aggregates & ValueAggregates.Percentiles) != 0 ? QuantileSketch.BucketOf(value) : default;
        accumulator.AddValueDetail(name, value, rawEvent.Ticks, aggregates, bucket);
      }
    }
    foreach (var dimension in source.DistinctDimensions)
    {
      if (rawEvent.TryGetDimension(dimension, out var value) && value != null)
        accumulator.AddDistinct(dimension, Text.StableHash(value), context.Precision);
    }
  }

  static void AddRawSession(
      QueryContext context,
      SourceContext source,
      Dictionary<GroupKey, RollupAccumulator> groups,
      RawSession session,
      string[] groupValues)
  {
    if (!MatchAndGroup(context, source, session.Dimensions, groupValues))
      return;
    var accumulator = GetGroup(groups, context.Calendar.Bucket(session.Start, context.Interval), groupValues);
    ++accumulator.Count;
    if (source.ReadsVisitors)
      accumulator.AddVisitor(session.Visitor, context.Precision);
    accumulator.AddValueDelta(StandardValues.Bounces, session.Interactions <= 1 ? 1 : 0);
    var duration = session.Last - session.Start;
    if (duration > 0)
      accumulator.AddValueDelta(StandardValues.Duration, (double)duration / TimeSpan.TicksPerSecond);
    if (session.PageViews > 0)
      accumulator.AddValueDelta(StandardValues.SessionPageViews, session.PageViews);
    foreach (var dimension in source.DistinctDimensions)
    {
      if (session.Dimensions.TryGetValue(dimension, out var value) && value != null)
        accumulator.AddDistinct(dimension, Text.StableHash(value), context.Precision);
    }
  }

  /// <summary>
  /// Entrances count at the first event of a session when it is a page view; exits count at
  /// the last page view of a session. This matches how ingestion maintains them in rollups.
  /// </summary>
  static void AddRawEntranceAndExit(
      QueryContext context,
      SourceContext source,
      Dictionary<GroupKey, RollupAccumulator> groups,
      RawSession session,
      string[] groupValues,
      ReadSegment segment,
      PageDimensions pageDimensions)
  {
    if (session.FirstIsPageView && session.Start >= segment.From && session.Start < segment.To)
    {
      var dimensions = pageDimensions.Of(session.Dimensions, session.FirstPage, session.FirstHostname);
      if (MatchAndGroup(context, source, dimensions, groupValues))
      {
        GetGroup(groups, context.Calendar.Bucket(session.Start, context.Interval), groupValues)
            .AddValueDelta(StandardValues.Entrances, 1);
      }
    }
    if (session.LastPage != null && session.LastPageTicks >= segment.From && session.LastPageTicks < segment.To)
    {
      var dimensions = pageDimensions.Of(session.Dimensions, session.LastPage, session.LastHostname);
      if (MatchAndGroup(context, source, dimensions, groupValues))
      {
        GetGroup(groups, context.Calendar.Bucket(session.LastPageTicks, context.Interval), groupValues)
            .AddValueDelta(StandardValues.Exits, 1);
      }
    }
  }

  static bool MatchAndGroup(
      QueryContext context, SourceContext source, IReadOnlyDictionary<string, string> dimensions, string[] groupValues)
  {
    foreach (var filter in source.Filters)
    {
      if (!filter.IsMatch(dimensions.GetValueOrDefault(filter.Dimension)))
        return false;
    }
    for (var i = 0; i < groupValues.Length; ++i)
      groupValues[i] = KeyCodec.Stored(dimensions.GetValueOrDefault(context.GroupBy[i]));
    return true;
  }

  #endregion

  #region Event names

  public IReadOnlyList<string> GetEventNames(
      DateRange range, string site, TimeZoneInfo timeZone, CancellationToken cancellationToken)
  {
    var now = DateTime.UtcNow.Ticks;
    var (from, to) = range.Resolve(preparation.CalendarFor(timeZone), now);
    site = enricher.NormalizeSite(site);
    var cutoffs = RetentionCutoffs.Compute(options.Retention, storageCalendar, now);
    var tiers = AvailableRollups(storageCalendar, TimeInterval.None, cutoffs);
    var plan = QueryRangePlanner.Build(storageCalendar, from, to, tiers, rawEvents != null, cancellationToken);
    var names = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var segment in plan.Segments)
    {
      if (segment.Source == PlanSource.RawEvents)
      {
        CollectRawEventNames(names, site, segment.From, segment.To, cancellationToken);
        continue;
      }
      var (fromBucket, toBucket) = segment.Buckets(storageCalendar);
      names.UnionWith(rollups.ListEventNames(segment.Granularity, site, fromBucket, toBucket, cancellationToken));
    }
    return [.. names];
  }

  void CollectRawEventNames(SortedSet<string> names, string site, long from, long to, CancellationToken cancellationToken)
  {
    var selector = new DimensionSelector([]);
    var slices = rawEvents.Scan(site, from, to, options.Queries.MaxDegreeOfParallelism,
        () => (Names: new SortedSet<string>(StringComparer.Ordinal), Event: new RawEvent()),
        (slice, ticks, value) =>
        {
          if (!RawEventCodec.TryDecode(value, ticks, null, default, slice.Event, selector))
            return;
          slice.Names.Add(slice.Event.Name);
          if (options.Sessions.Enabled && slice.Event.SessionId != 0 &&
              slice.Event.SessionStartTicks >= from && slice.Event.SessionStartTicks < to)
            slice.Names.Add(StandardEvents.Session);
        }, cancellationToken);
    foreach (var slice in slices)
      names.UnionWith(slice.Names);
  }

  #endregion
}
