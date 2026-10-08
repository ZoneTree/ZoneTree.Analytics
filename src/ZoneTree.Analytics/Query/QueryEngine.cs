using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>
/// Plans and executes queries over rollups and raw events.
/// </summary>
/// <remarks>
/// <para>
/// Planning: the query's dimension set (group-by plus filter dimensions) selects a rollup
/// definition. The time range is then covered greedily from coarse to fine levels:
/// whole local days from daily rollups, remaining whole hours from hourly rollups,
/// remaining whole minutes from minute rollups, and any remainder from raw events.
/// Each level is used only where it is enabled, retained and was already active.
/// </para>
/// <para>
/// Execution: every rollup segment is one contiguous ZoneTree range scan per partition.
/// Raw segments are scanned and aggregated on the fly, including session reconstruction
/// for session metrics, so raw answers match rollup answers.
/// </para>
/// </remarks>
sealed class QueryEngine
{
  enum Level
  {
    Day,
    Hour,
    Minute,
    Raw,
  }

  readonly record struct Segment(Level Level, long From, long To);

  readonly record struct GroupKey(long Bucket, string Values);

  readonly AnalyticsOptions options;

  readonly ReportingCalendar calendar;

  readonly RollupStore rollups;

  readonly RawEventStore rawEvents;

  readonly RollupDefinition[] definitions;

  readonly Func<string, long> definitionActiveSince;

  readonly EventEnricher enricher;

  public QueryEngine(
      AnalyticsOptions options,
      ReportingCalendar calendar,
      RollupStore rollups,
      RawEventStore rawEvents,
      RollupDefinition[] definitions,
      Func<string, long> definitionActiveSince,
      EventEnricher enricher)
  {
    this.options = options;
    this.calendar = calendar;
    this.rollups = rollups;
    this.rawEvents = rawEvents;
    this.definitions = definitions;
    this.definitionActiveSince = definitionActiveSince;
    this.enricher = enricher;
  }

  sealed class QueryContext
  {
    public string Site;

    public string Event;

    public long From;

    public long To;

    public TimeInterval Interval;

    public string[] GroupBy;

    public DimensionFilter[] Filters;

    public RollupDefinition Definition;

    public int[] GroupByIndexes;

    public int[] FilterIndexes;

    public Dictionary<GroupKey, RollupAccumulator> Groups = [];

    public bool UsedRollups;

    public bool UsedRawEvents;

    public bool MayBeIncomplete;

    public long EffectiveFrom = long.MaxValue;

    public long EffectiveTo = long.MinValue;

    public int Precision;
  }

  public AnalyticsResult Execute(AnalyticsQuery query)
  {
    ArgumentNullException.ThrowIfNull(query);
    var context = Prepare(query);
    var now = DateTime.UtcNow.Ticks;
    var segments = Plan(context, query.Source, now);
    foreach (var segment in segments)
    {
      context.EffectiveFrom = Math.Min(context.EffectiveFrom, segment.From);
      context.EffectiveTo = Math.Max(context.EffectiveTo, segment.To);
      if (segment.Level == Level.Raw)
        ExecuteRaw(context, segment);
      else
        ExecuteRollup(context, segment);
    }
    if (context.EffectiveFrom == long.MaxValue)
    {
      context.EffectiveFrom = context.From;
      context.EffectiveTo = context.To;
    }
    return BuildResult(context, query);
  }

  QueryContext Prepare(AnalyticsQuery query)
  {
    var eventName = Text.Name(query.Event, 128);
    if (eventName.Length == 0)
      throw new AnalyticsQueryException("Query event is required.");
    var from = ToUtcTicks(query.From);
    var to = ToUtcTicks(query.To);
    if (from >= to)
      throw new AnalyticsQueryException("Query range is empty: From must be earlier than To.");
    var groupBy = (query.GroupByDimensions ?? [])
        .Select(x => x?.Trim())
        .Where(x => !string.IsNullOrEmpty(x))
        .ToArray();
    if (groupBy.Distinct(StringComparer.Ordinal).Count() != groupBy.Length)
      throw new AnalyticsQueryException("Group-by dimensions must be distinct.");
    if (groupBy.Length > 8)
      throw new AnalyticsQueryException("At most 8 group-by dimensions are supported.");
    var filters = (query.Filters ?? []).Where(x => x != null).ToArray();
    var dimensionSet = groupBy
        .Concat(filters.Select(x => x.Dimension))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();
    var setId = string.Join(",", dimensionSet);
    var definition = definitions.FirstOrDefault(x =>
        x.Id == setId && (x.Events == null || x.Events.Contains(eventName)));
    var context = new QueryContext
    {
      Site = enricher.NormalizeSite(query.Site),
      Event = eventName,
      From = from,
      To = to,
      Interval = query.Interval,
      GroupBy = groupBy,
      Filters = filters,
      Definition = definition,
      Precision = options.Rollups.HyperLogLogPrecision
    };
    if (definition != null)
    {
      context.GroupByIndexes = groupBy.Select(x => IndexOf(definition.Dimensions, x)).ToArray();
      context.FilterIndexes = filters.Select(x => IndexOf(definition.Dimensions, x.Dimension)).ToArray();
    }
    return context;
  }

  static int IndexOf(IReadOnlyList<string> list, string value)
  {
    for (var i = 0; i < list.Count; ++i)
    {
      if (list[i] == value)
        return i;
    }
    return -1;
  }

  static long ToUtcTicks(DateTime value) =>
      value.Kind == DateTimeKind.Local ? value.ToUniversalTime().Ticks : value.Ticks;

  #region Planning

  List<Segment> Plan(QueryContext context, QuerySource source, long now)
  {
    var levels = new List<Level>(4);
    if (context.Definition != null && source != QuerySource.RawEvents)
    {
      var interval = context.Interval;
      if (interval is not (TimeInterval.Minute or TimeInterval.Hour) && rollups.IsEnabled(Granularity.Day))
        levels.Add(Level.Day);
      if (interval != TimeInterval.Minute && rollups.IsEnabled(Granularity.Hour))
        levels.Add(Level.Hour);
      if (rollups.IsEnabled(Granularity.Minute))
        levels.Add(Level.Minute);
    }
    var rawEnabled = source != QuerySource.Rollups && options.Storage.StoreRawEvents && rawEvents != null;
    if (rawEnabled)
      levels.Add(Level.Raw);
    if (levels.Count == 0)
    {
      if (source == QuerySource.RawEvents)
        throw new AnalyticsQueryException("Raw events are not stored (StorageOptions.StoreRawEvents is false).");
      if (context.Definition == null)
        throw new AnalyticsQueryException(
            $"No rollup covers the dimension set [{string.Join(", ", context.GroupBy.Concat(context.Filters.Select(x => x.Dimension)).Distinct())}] " +
            $"for event '{context.Event}' and raw events are not available. Add a RollupDefinition with exactly these dimensions.");
      throw new AnalyticsQueryException(
          $"No rollup granularity can answer interval {context.Interval}. Enable a finer granularity.");
    }
    var segments = new List<Segment>();
    Cover(context, levels, segments, context.From, context.To, 0, true, true, now);
    return segments;
  }

  void Cover(
      QueryContext context,
      List<Level> levels,
      List<Segment> segments,
      long from,
      long to,
      int levelIndex,
      bool fromIsQueryBound,
      bool toIsQueryBound,
      long now)
  {
    if (from >= to)
      return;
    if (levelIndex == levels.Count)
    {
      Fallback(context, levels, segments, from, to, fromIsQueryBound, toIsQueryBound, now);
      return;
    }
    var level = levels[levelIndex];
    if (level == Level.Raw)
    {
      if (!IsRawRetained(from, now))
        context.MayBeIncomplete = true;
      segments.Add(new Segment(Level.Raw, from, to));
      return;
    }
    var alignedFrom = Ceil(level, from);
    var alignedTo = Floor(level, to);
    if (alignedFrom < alignedTo && IsAvailable(context, level, alignedFrom, now))
    {
      segments.Add(new Segment(level, alignedFrom, alignedTo));
      Cover(context, levels, segments, from, alignedFrom, levelIndex + 1, fromIsQueryBound, false, now);
      Cover(context, levels, segments, alignedTo, to, levelIndex + 1, false, toIsQueryBound, now);
      return;
    }
    Cover(context, levels, segments, from, to, levelIndex + 1, fromIsQueryBound, toIsQueryBound, now);
  }

  /// <summary>
  /// No level can cover [from, to) exactly. Use the coarsest retained rollup level and
  /// widen the query-side bounds to its buckets, never overlapping neighbor segments.
  /// </summary>
  void Fallback(
      QueryContext context,
      List<Level> levels,
      List<Segment> segments,
      long from,
      long to,
      bool fromIsQueryBound,
      bool toIsQueryBound,
      long now)
  {
    context.MayBeIncomplete = true;
    var rollupLevels = levels.Where(x => x != Level.Raw).ToList();
    if (rollupLevels.Count == 0)
      return;
    var level = rollupLevels.FirstOrDefault(x => IsAvailable(context, x, Floor(x, from), now), rollupLevels[^1]);
    var start = fromIsQueryBound ? Floor(level, from) : Ceil(level, from);
    var end = toIsQueryBound ? Ceil(level, to) : Floor(level, to);
    if (start < end)
      segments.Add(new Segment(level, start, end));
  }

  long Ceil(Level level, long ticks) => level switch
  {
    Level.Day => calendar.CeilToDayBoundary(ticks),
    Level.Hour => CeilTicks(ticks, TimeSpan.TicksPerHour),
    Level.Minute => CeilTicks(ticks, TimeSpan.TicksPerMinute),
    _ => ticks
  };

  long Floor(Level level, long ticks) => level switch
  {
    Level.Day => calendar.FloorToDayBoundary(ticks),
    Level.Hour => ReportingCalendar.FloorTicks(ticks, TimeSpan.TicksPerHour),
    Level.Minute => ReportingCalendar.FloorTicks(ticks, TimeSpan.TicksPerMinute),
    _ => ticks
  };

  static long CeilTicks(long ticks, long unit)
  {
    var floor = ReportingCalendar.FloorTicks(ticks, unit);
    return floor == ticks ? ticks : floor + unit;
  }

  bool IsAvailable(QueryContext context, Level level, long from, long now)
  {
    if (from < definitionActiveSince(context.Definition.Id))
      return false;
    var retention = level switch
    {
      Level.Day => options.Retention.DayRollups,
      Level.Hour => options.Retention.HourRollups,
      _ => options.Retention.MinuteRollups
    };
    return retention == null || from >= now - retention.Value.Ticks;
  }

  bool IsRawRetained(long from, long now)
  {
    var retention = options.Retention.RawEvents;
    return retention == null || from >= now - retention.Value.Ticks;
  }

  #endregion

  #region Rollup execution

  void ExecuteRollup(QueryContext context, Segment segment)
  {
    context.UsedRollups = true;
    var definition = context.Definition;
    var dimensionCount = definition.Dimensions.Count;
    Granularity granularity;
    long fromBucket, toBucket;
    switch (segment.Level)
    {
      case Level.Day:
        granularity = Granularity.Day;
        fromBucket = calendar.LocalDay(segment.From);
        toBucket = calendar.LocalDay(segment.To);
        if (calendar.UtcStartOfLocalDay(toBucket) < segment.To)
          toBucket += TimeSpan.TicksPerDay;
        break;
      case Level.Hour:
        granularity = Granularity.Hour;
        fromBucket = segment.From;
        toBucket = segment.To;
        break;
      default:
        granularity = Granularity.Minute;
        fromBucket = segment.From;
        toBucket = segment.To;
        break;
    }
    var isDay = segment.Level == Level.Day;
    var interval = context.Interval;
    var groupValues = new string[context.GroupBy.Length];
    rollups.Scan(granularity, context.Site, context.Event, definition.Id, fromBucket, toBucket,
        (bucket, combined, value) =>
        {
          var parts = KeyCodec.SplitValues(combined, dimensionCount);
          for (var i = 0; i < context.Filters.Length; ++i)
          {
            if (!context.Filters[i].Matches(parts[context.FilterIndexes[i]]))
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
            groupValues[i] = parts[context.GroupByIndexes[i]];
          GetGroup(context, outputBucket, groupValues).Merge(value);
        });
  }

  static RollupAccumulator GetGroup(QueryContext context, long bucket, string[] values)
  {
    var combined = values.Length switch
    {
      0 => "",
      1 => values[0],
      _ => string.Join(KeyCodec.ValueSeparator, values)
    };
    var key = new GroupKey(bucket, combined);
    if (!context.Groups.TryGetValue(key, out var accumulator))
    {
      accumulator = new RollupAccumulator();
      context.Groups.Add(key, accumulator);
    }
    return accumulator;
  }

  #endregion

  #region Raw execution

  sealed class RawSession
  {
    public long Start;

    public long Last;

    public int PageViews;

    public int Interactions;

    public ulong Visitor;

    public Dictionary<string, string> Dimensions;

    public bool FirstIsPageView;

    public string FirstPage;

    public string FirstHostname;

    public string LastPage;

    public string LastHostname;

    public long LastPageTicks;
  }

  void ExecuteRaw(QueryContext context, Segment segment)
  {
    context.UsedRawEvents = true;
    var isSessionQuery = context.Event == StandardEvents.Session;
    var isPageViewQuery = context.Event == StandardEvents.PageView;
    var trackSessions = (isSessionQuery || isPageViewQuery) && options.Sessions.Enabled;
    if (isSessionQuery && !trackSessions)
      return;

    var rawEvent = new RawEvent();
    var groupValues = new string[context.GroupBy.Length];
    var eventNameUtf8 = RawEventCodec.Utf8(context.Event);
    var sessions = trackSessions ? new Dictionary<ulong, RawSession>() : null;
    var scanTo = segment.To;
    if (trackSessions)
    {
      // Session metrics are attributed to the session start. Scan past the segment end so
      // sessions that start inside the segment are seen to completion.
      var extension = options.Sessions.Timeout.Ticks * 4;
      scanTo = Math.Min(segment.To + extension, DateTime.UtcNow.Ticks + options.Ingestion.MaxFutureSkew.Ticks);
      scanTo = Math.Max(scanTo, segment.To);
    }

    var neededDimensions = context.GroupBy.Concat(context.Filters.Select(x => x.Dimension));
    if (trackSessions)
      neededDimensions = neededDimensions.Append(StandardDimensions.Page).Append(StandardDimensions.Hostname);
    var selector = new DimensionSelector(neededDimensions);

    rawEvents.Scan(context.Site, segment.From, scanTo, (ticks, _, value) =>
    {
      var decoded = trackSessions
          ? RawEventCodec.TryDecode(value.Span, ticks, null, default, rawEvent, selector)
          : RawEventCodec.TryDecode(value.Span, ticks, context.Event, eventNameUtf8, rawEvent, selector);
      if (!decoded)
        return true;
      if (!isSessionQuery && ticks < segment.To && rawEvent.Name == context.Event)
        AddRawEvent(context, rawEvent, groupValues);
      // Session metrics count sessions that start in the segment. Exits count wherever the
      // last page view of a session falls, so page view queries track every session seen.
      if (trackSessions && rawEvent.SessionId != 0 &&
          (isPageViewQuery || (rawEvent.SessionStartTicks >= segment.From && rawEvent.SessionStartTicks < segment.To)))
        TrackSession(sessions, rawEvent);
      return true;
    });

    if (sessions == null)
      return;
    foreach (var session in sessions.Values)
    {
      if (session.Dimensions == null)
        continue;
      if (isSessionQuery)
        AddRawSession(context, session, groupValues);
      else
        AddRawEntranceAndExit(context, session, groupValues, segment);
    }
  }

  void AddRawEvent(QueryContext context, RawEvent rawEvent, string[] groupValues)
  {
    foreach (var filter in context.Filters)
    {
      if (!rawEvent.TryGetDimension(filter.Dimension, out var value) || !filter.Matches(value))
        return;
    }
    for (var i = 0; i < groupValues.Length; ++i)
    {
      if (!rawEvent.TryGetDimension(context.GroupBy[i], out groupValues[i]))
        return;
    }
    var bucket = calendar.Bucket(rawEvent.Ticks, context.Interval);
    var accumulator = GetGroup(context, bucket, groupValues);
    ++accumulator.Count;
    accumulator.AddVisitor(rawEvent.Visitor, context.Precision);
    foreach (var (name, value) in rawEvent.Metrics)
      accumulator.AddMetric(name, value);
  }

  static void TrackSession(Dictionary<ulong, RawSession> sessions, RawEvent rawEvent)
  {
    if (!sessions.TryGetValue(rawEvent.SessionId, out var session))
    {
      session = new RawSession
      {
        Start = rawEvent.SessionStartTicks,
        Last = rawEvent.Ticks,
        Visitor = rawEvent.Visitor
      };
      sessions.Add(rawEvent.SessionId, session);
    }
    var isFirstEvent = rawEvent.Ticks == session.Start && session.Dimensions == null;
    if (session.Dimensions == null)
    {
      var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var (key, value) in rawEvent.Dimensions)
      {
        if (key == StandardDimensions.EntryPage || EventEnricher.IsSessionLevel(key))
          dimensions[key] = value;
      }
      session.Dimensions = dimensions;
    }
    if (rawEvent.Ticks > session.Last)
      session.Last = rawEvent.Ticks;
    if (rawEvent.Interactive)
      ++session.Interactions;
    var isPageView = rawEvent.Name == StandardEvents.PageView;
    if (!isPageView)
      return;
    ++session.PageViews;
    if (!rawEvent.TryGetDimension(StandardDimensions.Page, out var page))
      return;
    rawEvent.TryGetDimension(StandardDimensions.Hostname, out var hostname);
    if (isFirstEvent)
    {
      session.FirstIsPageView = true;
      session.FirstPage = page;
      session.FirstHostname = hostname ?? "";
    }
    if (session.LastPage == null || rawEvent.Ticks >= session.LastPageTicks)
    {
      session.LastPage = page;
      session.LastHostname = hostname ?? "";
      session.LastPageTicks = rawEvent.Ticks;
    }
  }

  void AddRawSession(QueryContext context, RawSession session, string[] groupValues)
  {
    if (!MatchAndGroup(context, session.Dimensions, groupValues))
      return;
    var bucket = calendar.Bucket(session.Start, context.Interval);
    var accumulator = GetGroup(context, bucket, groupValues);
    ++accumulator.Count;
    accumulator.AddVisitor(session.Visitor, context.Precision);
    accumulator.AddMetricDelta(StandardMetrics.Bounces, session.Interactions <= 1 ? 1 : 0);
    var duration = session.Last - session.Start;
    if (duration > 0)
      accumulator.AddMetricDelta(StandardMetrics.Duration, (double)duration / TimeSpan.TicksPerSecond);
    if (session.PageViews > 0)
      accumulator.AddMetricDelta(StandardMetrics.SessionPageViews, session.PageViews);
  }

  /// <summary>
  /// Entrances count at the first event of a session when it is a page view; exits count at
  /// the last page view of a session. This matches how ingestion maintains them in rollups.
  /// </summary>
  void AddRawEntranceAndExit(QueryContext context, RawSession session, string[] groupValues, Segment segment)
  {
    if (session.FirstIsPageView && session.Start >= segment.From && session.Start < segment.To)
    {
      var dimensions = WithPage(session.Dimensions, session.FirstPage, session.FirstHostname);
      if (MatchAndGroup(context, dimensions, groupValues))
      {
        GetGroup(context, calendar.Bucket(session.Start, context.Interval), groupValues)
            .AddMetricDelta(StandardMetrics.Entrances, 1);
      }
    }
    if (session.LastPage != null && session.LastPageTicks >= segment.From && session.LastPageTicks < segment.To)
    {
      var dimensions = WithPage(session.Dimensions, session.LastPage, session.LastHostname);
      if (MatchAndGroup(context, dimensions, groupValues))
      {
        GetGroup(context, calendar.Bucket(session.LastPageTicks, context.Interval), groupValues)
            .AddMetricDelta(StandardMetrics.Exits, 1);
      }
    }
  }

  static Dictionary<string, string> WithPage(
      Dictionary<string, string> dimensions, string page, string hostname)
  {
    return new Dictionary<string, string>(dimensions, StringComparer.Ordinal)
    {
      [StandardDimensions.Page] = page,
      [StandardDimensions.Hostname] = hostname
    };
  }

  static bool MatchAndGroup(QueryContext context, Dictionary<string, string> dimensions, string[] groupValues)
  {
    foreach (var filter in context.Filters)
    {
      if (!dimensions.TryGetValue(filter.Dimension, out var value) || !filter.Matches(value))
        return false;
    }
    for (var i = 0; i < groupValues.Length; ++i)
    {
      if (!dimensions.TryGetValue(context.GroupBy[i], out groupValues[i]))
        return false;
    }
    return true;
  }

  #endregion

  #region Results

  AnalyticsResult BuildResult(QueryContext context, AnalyticsQuery query)
  {
    var rows = new List<(GroupKey Key, string[] Values, RollupAccumulator Accumulator)>(context.Groups.Count);
    var totals = new RollupAccumulator();
    foreach (var (key, accumulator) in context.Groups)
    {
      if (accumulator.IsEmpty)
        continue;
      totals.Merge(accumulator);
      rows.Add((key, KeyCodec.SplitValues(key.Values, context.GroupBy.Length), accumulator));
    }

    var interval = context.Interval;
    if (interval != TimeInterval.None && context.GroupBy.Length == 0 && query.FillEmptyBuckets)
    {
      var existing = new HashSet<long>(rows.Select(x => x.Key.Bucket));
      foreach (var bucket in EnumerateBuckets(context.From, context.To, interval))
      {
        if (existing.Add(bucket))
          rows.Add((new GroupKey(bucket, ""), [], new RollupAccumulator()));
      }
    }

    var order = query.OrderBy ??
        (interval != TimeInterval.None ? QueryOrder.ByBucket() : QueryOrder.ByEvents());
    var groupByIndex = order.Kind == OrderKind.Dimension
        ? Array.IndexOf(context.GroupBy, order.Name)
        : -1;
    if (order.Kind == OrderKind.Dimension && groupByIndex < 0)
      throw new AnalyticsQueryException($"Cannot order by '{order.Name}': it is not a group-by dimension.");

    var materialized = rows.Select(x => (
        Row: new AnalyticsRow(
            interval == TimeInterval.None ? null : calendar.BucketLabel(x.Key.Bucket, interval),
            x.Values,
            x.Accumulator.Count,
            x.Accumulator.Visitors?.Estimate() ?? 0,
            Snapshot(x.Accumulator)),
        x.Key.Bucket)).ToList();

    int Compare((AnalyticsRow Row, long Bucket) a, (AnalyticsRow Row, long Bucket) b)
    {
      var result = order.Kind switch
      {
        OrderKind.Events => a.Row.Events.CompareTo(b.Row.Events),
        OrderKind.Visitors => a.Row.Visitors.CompareTo(b.Row.Visitors),
        OrderKind.MetricSum => a.Row.Sum(order.Name).CompareTo(b.Row.Sum(order.Name)),
        OrderKind.MetricAverage => a.Row.Metric(order.Name).Average.CompareTo(b.Row.Metric(order.Name).Average),
        OrderKind.Dimension => string.CompareOrdinal(a.Row.Dimensions[groupByIndex], b.Row.Dimensions[groupByIndex]),
        _ => a.Bucket.CompareTo(b.Bucket)
      };
      if (order.Descending)
        result = -result;
      if (result != 0)
        return result;
      // Deterministic tie-breakers: bucket, events descending, dimension values.
      result = a.Bucket.CompareTo(b.Bucket);
      if (result != 0)
        return result;
      result = b.Row.Events.CompareTo(a.Row.Events);
      if (result != 0)
        return result;
      for (var i = 0; i < a.Row.Dimensions.Count; ++i)
      {
        result = string.CompareOrdinal(a.Row.Dimensions[i], b.Row.Dimensions[i]);
        if (result != 0)
          return result;
      }
      return 0;
    }

    materialized.Sort(Compare);
    IEnumerable<AnalyticsRow> ordered = materialized.Select(x => x.Row);
    if (query.Limit.HasValue)
      ordered = ordered.Take(Math.Max(0, query.Limit.Value));

    var totalsRow = new AnalyticsRow(
        null, [], totals.Count, totals.Visitors?.Estimate() ?? 0, Snapshot(totals));
    return new AnalyticsResult(
        context.GroupBy,
        interval,
        ordered.ToList(),
        totalsRow,
        context.UsedRollups,
        context.UsedRawEvents,
        context.MayBeIncomplete,
        new DateTime(Math.Min(context.EffectiveFrom, context.From), DateTimeKind.Utc),
        new DateTime(Math.Max(context.EffectiveTo, context.To), DateTimeKind.Utc));
  }

  static IReadOnlyDictionary<string, MetricValue> Snapshot(RollupAccumulator accumulator)
  {
    if (accumulator.Metrics.Count == 0)
      return null;
    return new Dictionary<string, MetricValue>(accumulator.Metrics, StringComparer.Ordinal);
  }

  IEnumerable<long> EnumerateBuckets(long from, long to, TimeInterval interval)
  {
    const int maxBuckets = 100_000;
    var bucket = calendar.Bucket(from, interval);
    var isUtcInterval = interval is TimeInterval.Minute or TimeInterval.Hour;
    for (var i = 0; i < maxBuckets; ++i)
    {
      var start = isUtcInterval ? bucket : calendar.UtcStartOfLocalDay(bucket);
      if (start >= to)
        yield break;
      yield return bucket;
      bucket = calendar.NextBucket(bucket, interval);
    }
  }

  #endregion
}
