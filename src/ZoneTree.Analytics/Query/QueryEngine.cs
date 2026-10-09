using System.Globalization;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>
/// Plans and executes queries over rollups and raw events.
/// </summary>
/// <remarks>
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
  enum Level
  {
    Day,
    Hour,
    Minute,
    Raw,
  }

  readonly record struct PlanSegment(Level Level, long From, long To);

  readonly record struct GroupKey(long Bucket, string Values);

  const int CancellationCheckInterval = 1024;

  // Time series longer than this are rejected rather than truncated.
  const int MaxBuckets = 100_000;

  readonly AnalyticsOptions options;

  readonly ReportingCalendar storageCalendar;

  readonly RollupStore rollups;

  readonly RawEventStore rawEvents;

  readonly RollupDefinition[] definitions;

  readonly Func<string, long> definitionActiveSince;

  readonly EnrichmentPipeline enricher;

  public QueryEngine(
      AnalyticsOptions options,
      ReportingCalendar storageCalendar,
      RollupStore rollups,
      RawEventStore rawEvents,
      RollupDefinition[] definitions,
      Func<string, long> definitionActiveSince,
      EnrichmentPipeline enricher)
  {
    this.options = options;
    this.storageCalendar = storageCalendar;
    this.rollups = rollups;
    this.rawEvents = rawEvents;
    this.definitions = definitions;
    this.definitionActiveSince = definitionActiveSince;
    this.enricher = enricher;
  }

  #region Context

  sealed class QueryContext
  {
    public string Site;

    public string BaseEvent;

    public ReportingCalendar Calendar;

    public bool DayRollupsCompatible;

    public long From;

    public long To;

    public TimeInterval Interval;

    public string[] GroupBy;

    public Filter[] Filters;

    public Segment[] Segments;

    public Metric[] Metrics;

    public Dictionary<string, SourceContext> Sources = new(StringComparer.Ordinal);

    public List<QueryPlanStep> Steps = [];

    public HashSet<string> Warnings = new(StringComparer.Ordinal);

    public SegmentSets SegmentSets;

    public int Precision;

    public CancellationToken CancellationToken;

    public long NowTicks;
  }

  sealed class SourceContext
  {
    public string Event;

    public byte[] EventUtf8;

    public RollupDefinition Definition;

    public int[] GroupByIndexes;

    public int[] FilterIndexes;

    public Dictionary<GroupKey, RollupAccumulator> Groups = [];

    public List<PlanSegment> Plan = [];
  }

  /// <summary>
  /// The visitors and sessions selected by each behavioral segment.
  /// </summary>
  sealed class SegmentSets
  {
    public Segment[] Segments;

    public HashSet<ulong>[] Members;

    public bool Allows(ulong visitor, ulong session)
    {
      for (var i = 0; i < Segments.Length; ++i)
      {
        var segment = Segments[i];
        var id = segment.Scope == SegmentScope.Visitors ? visitor : session;
        var isMember = id != 0 && Members[i].Contains(id);
        if (isMember == segment.IsExcluded)
          return false;
      }
      return true;
    }
  }

  ReportingCalendar CalendarFor(TimeZoneInfo timeZone)
  {
    if (timeZone == null || timeZone.Id == storageCalendar.TimeZone.Id)
      return storageCalendar;
    return new ReportingCalendar(timeZone, options.FirstDayOfWeek);
  }

  #endregion

  #region Entry points

  public AnalyticsResult Execute(AnalyticsQuery query, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(query);
    var now = DateTime.UtcNow.Ticks;
    var (result, bucketKeys) = ExecuteCore(query, now, cancellationToken);
    if (query.Compare == null)
      return result;

    var calendar = CalendarFor(query.TimeZone);
    var previousRange = query.Compare.Resolve(query.Range, calendar, now);
    var (previous, previousBucketKeys) = ExecuteCore(query.CloneForRange(previousRange), now, cancellationToken);
    AttachComparison(result, bucketKeys, previous, previousBucketKeys);
    return result;
  }

  (AnalyticsResult Result, Dictionary<AnalyticsRow, int> BucketIndexes) ExecuteCore(
      AnalyticsQuery query, long now, CancellationToken cancellationToken)
  {
    var context = Prepare(query, now, cancellationToken);
    if (context.Segments.Length > 0)
      context.SegmentSets = EvaluateSegments(context);
    foreach (var source in context.Sources.Values)
      Plan(context, source, query.Source);
    Execute(context);
    return BuildResult(context, query);
  }

  #endregion

  #region Preparation

  QueryContext Prepare(AnalyticsQuery query, long now, CancellationToken cancellationToken)
  {
    var baseEvent = Text.Name(query.Event, 128);
    if (baseEvent.Length == 0)
      throw new AnalyticsQueryException("The query event is required.");
    var calendar = CalendarFor(query.TimeZone);
    var (from, to) = query.Range.Resolve(calendar, now);
    if (from >= to)
      throw new AnalyticsQueryException("The query range is empty.");
    if (query.Interval != TimeInterval.None &&
        EnumerateBuckets(calendar, from, to, query.Interval).Skip(MaxBuckets).Any())
    {
      throw new AnalyticsQueryException(
          $"The range has more than {MaxBuckets.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {query.Interval.ToString().ToLowerInvariant()} buckets. Use a larger interval or a shorter range.");
    }

    var groupBy = query.GroupBy
        .Select(x => x?.Trim())
        .Where(x => !string.IsNullOrEmpty(x))
        .ToArray();
    if (groupBy.Distinct(StringComparer.Ordinal).Count() != groupBy.Length)
      throw new AnalyticsQueryException("Group-by dimensions must be distinct.");
    if (groupBy.Length > 8)
      throw new AnalyticsQueryException("At most 8 group-by dimensions are supported.");
    var filters = query.Filters.Where(x => x != null).ToArray();
    var segments = query.Segments.Where(x => x != null).ToArray();
    var metrics = query.Metrics.Where(x => x != null).Distinct().ToArray();
    if (metrics.Length == 0)
      metrics = [Metric.Events, Metric.Visitors];

    var dimensions = groupBy.Concat(filters.Select(x => x.Dimension)).Distinct(StringComparer.Ordinal).ToArray();
    foreach (var metric in metrics)
    {
      ValidateScope(metric, baseEvent, dimensions);
      if (segments.Length > 0 && metric.ScopeFor(baseEvent) == DimensionScope.Bot)
        throw new AnalyticsQueryException(
            $"The metric '{metric.Key}' cannot be combined with segments: bot events carry no visitor.");
    }
    foreach (var order in query.OrderBy)
    {
      if (order == null)
        continue;
      if (order.Metric != null && !metrics.Contains(order.Metric))
        throw new AnalyticsQueryException($"Cannot order by '{order.Metric.Key}': it is not one of the query metrics.");
      if (order.Dimension != null && !groupBy.Contains(order.Dimension))
        throw new AnalyticsQueryException($"Cannot order by '{order.Dimension}': it is not a group-by dimension.");
    }
    if (query.Limit is < 0)
      throw new AnalyticsQueryException("Limit cannot be negative.");
    if (query.Offset < 0)
      throw new AnalyticsQueryException("Offset cannot be negative.");

    var context = new QueryContext
    {
      Site = enricher.NormalizeSite(query.Site),
      BaseEvent = baseEvent,
      Calendar = calendar,
      DayRollupsCompatible = calendar.TimeZone.Id == storageCalendar.TimeZone.Id,
      From = from,
      To = to,
      Interval = query.Interval,
      GroupBy = groupBy,
      Filters = filters,
      Segments = segments,
      Metrics = metrics,
      Precision = options.Rollups.HyperLogLogPrecision,
      CancellationToken = cancellationToken,
      NowTicks = now
    };

    var dimensionSetId = string.Join(",", dimensions.OrderBy(x => x, StringComparer.Ordinal));
    foreach (var eventName in metrics.SelectMany(x => x.Sources(baseEvent)).Distinct(StringComparer.Ordinal))
    {
      var definition = definitions.FirstOrDefault(x =>
          x.Id == dimensionSetId && (x.Events == null || x.Events.Contains(eventName)));
      var source = new SourceContext
      {
        Event = eventName,
        EventUtf8 = RawEventCodec.Utf8(eventName),
        Definition = definition
      };
      if (definition != null)
      {
        source.GroupByIndexes = groupBy.Select(x => IndexOf(definition.Dimensions, x)).ToArray();
        source.FilterIndexes = filters.Select(x => IndexOf(definition.Dimensions, x.Dimension)).ToArray();
      }
      context.Sources.Add(eventName, source);
    }
    return context;
  }

  void ValidateScope(Metric metric, string baseEvent, string[] dimensions)
  {
    var scope = metric.ScopeFor(baseEvent);
    if (scope == DimensionScope.All)
      return;
    foreach (var dimension in dimensions)
    {
      if (enricher.Schema.Contains(scope, dimension))
        continue;
      var (allowed, hint) = scope switch
      {
        DimensionScope.Session => (
            "session-level dimensions and the entry page",
            "Use a Segment to select sessions or visitors by other conditions."),
        DimensionScope.PageView => (
            "session-level dimensions, the entry page, page and hostname",
            "Use a Segment to select sessions or visitors by other conditions."),
        _ => (
            "page, hostname, event, bot and bot_category",
            "Bot events carry no visitor, session or custom dimensions.")
      };
      throw new AnalyticsQueryException(
          $"The metric '{metric.Key}' can only be grouped and filtered by {allowed}, not by '{dimension}'. {hint}");
    }
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

  #endregion

  #region Planning

  void Plan(QueryContext context, SourceContext source, QuerySource selection)
  {
    var levels = new List<Level>(4);
    var rawEnabled = selection != QuerySource.Rollups && options.Storage.StoreRawEvents && rawEvents != null;
    if (context.Segments.Length > 0)
    {
      if (!rawEnabled)
        throw new AnalyticsQueryException("Segments require raw events, which are not available for this query.");
      levels.Add(Level.Raw);
    }
    else
    {
      if (source.Definition != null && selection != QuerySource.RawEvents)
      {
        var interval = context.Interval;
        if (context.DayRollupsCompatible &&
            interval is not (TimeInterval.Minute or TimeInterval.Hour) &&
            rollups.IsEnabled(Granularity.Day))
          levels.Add(Level.Day);
        if (interval != TimeInterval.Minute && rollups.IsEnabled(Granularity.Hour))
          levels.Add(Level.Hour);
        if (rollups.IsEnabled(Granularity.Minute))
          levels.Add(Level.Minute);
      }
      if (rawEnabled)
        levels.Add(Level.Raw);
    }
    if (levels.Count == 0)
    {
      if (selection == QuerySource.RawEvents)
        throw new AnalyticsQueryException("Raw events are not stored (StorageOptions.StoreRawEvents is false).");
      if (source.Definition == null)
      {
        var dimensions = context.GroupBy.Concat(context.Filters.Select(x => x.Dimension)).Distinct();
        throw new AnalyticsQueryException(
            $"No rollup covers the dimensions [{string.Join(", ", dimensions)}] for the event '{source.Event}', " +
            "and raw events are not available. Add a RollupDefinition with exactly these dimensions.");
      }
      throw new AnalyticsQueryException(
          $"No rollup granularity can answer the {context.Interval} interval. Enable a finer granularity.");
    }
    Cover(context, source, levels, context.From, context.To, 0, true, true);
  }

  void Cover(
      QueryContext context,
      SourceContext source,
      List<Level> levels,
      long from,
      long to,
      int levelIndex,
      bool fromIsQueryBound,
      bool toIsQueryBound)
  {
    if (from >= to)
      return;
    if (levelIndex == levels.Count)
    {
      Fallback(context, source, levels, from, to, fromIsQueryBound, toIsQueryBound);
      return;
    }
    var level = levels[levelIndex];
    if (level == Level.Raw)
    {
      CheckRawRetention(context, from);
      source.Plan.Add(new PlanSegment(Level.Raw, from, to));
      return;
    }
    var alignedFrom = Ceil(context, level, from);
    var alignedTo = Floor(context, level, to);
    if (alignedFrom < alignedTo && IsAvailable(context, source, level, alignedFrom))
    {
      source.Plan.Add(new PlanSegment(level, alignedFrom, alignedTo));
      Cover(context, source, levels, from, alignedFrom, levelIndex + 1, fromIsQueryBound, false);
      Cover(context, source, levels, alignedTo, to, levelIndex + 1, false, toIsQueryBound);
      return;
    }
    Cover(context, source, levels, from, to, levelIndex + 1, fromIsQueryBound, toIsQueryBound);
  }

  /// <summary>
  /// No level can cover [from, to) exactly. Use the coarsest available rollup level and widen
  /// the query-side bounds to its buckets, never overlapping neighbor segments.
  /// </summary>
  void Fallback(
      QueryContext context,
      SourceContext source,
      List<Level> levels,
      long from,
      long to,
      bool fromIsQueryBound,
      bool toIsQueryBound)
  {
    var rollupLevels = levels.Where(x => x != Level.Raw).ToList();
    if (rollupLevels.Count == 0)
    {
      context.Warnings.Add($"Part of the range of '{source.Event}' could not be answered.");
      return;
    }
    var level = rollupLevels.FirstOrDefault(
        x => IsAvailable(context, source, x, Floor(context, x, from)), rollupLevels[^1]);
    var start = fromIsQueryBound ? Floor(context, level, from) : Ceil(context, level, from);
    var end = toIsQueryBound ? Ceil(context, level, to) : Floor(context, level, to);
    if (start < end)
      source.Plan.Add(new PlanSegment(level, start, end));
    context.Warnings.Add(
        $"Parts of the range of '{source.Event}' were answered with whole {level.ToString().ToLowerInvariant()} buckets " +
        "because finer data is not available.");
  }

  void CheckRawRetention(QueryContext context, long from)
  {
    var retention = options.Retention.RawEvents;
    if (retention != null && from < context.NowTicks - retention.Value.Ticks)
    {
      var cutoff = new DateTime(context.NowTicks - retention.Value.Ticks, DateTimeKind.Utc);
      context.Warnings.Add(
          $"Raw events before {cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} are outside retention; the result may be incomplete.");
    }
  }

  long Ceil(QueryContext context, Level level, long ticks) => level switch
  {
    Level.Day => context.Calendar.CeilToDayBoundary(ticks),
    Level.Hour => CeilTicks(ticks, TimeSpan.TicksPerHour),
    Level.Minute => CeilTicks(ticks, TimeSpan.TicksPerMinute),
    _ => ticks
  };

  static long Floor(QueryContext context, Level level, long ticks) => level switch
  {
    Level.Day => context.Calendar.FloorToDayBoundary(ticks),
    Level.Hour => ReportingCalendar.FloorTicks(ticks, TimeSpan.TicksPerHour),
    Level.Minute => ReportingCalendar.FloorTicks(ticks, TimeSpan.TicksPerMinute),
    _ => ticks
  };

  static long CeilTicks(long ticks, long unit)
  {
    var floor = ReportingCalendar.FloorTicks(ticks, unit);
    return floor == ticks ? ticks : floor + unit;
  }

  bool IsAvailable(QueryContext context, SourceContext source, Level level, long from)
  {
    if (from < definitionActiveSince(source.Definition.Id))
      return false;
    var retention = level switch
    {
      Level.Day => options.Retention.DayRollups,
      Level.Hour => options.Retention.HourRollups,
      _ => options.Retention.MinuteRollups
    };
    return retention == null || from >= context.NowTicks - retention.Value.Ticks;
  }

  #endregion

  #region Execution

  void Execute(QueryContext context)
  {
    // Raw parts of several sources over the same range share one scan.
    var rawBatches = new Dictionary<(long, long), List<SourceContext>>();
    foreach (var source in context.Sources.Values)
    {
      foreach (var segment in source.Plan)
      {
        context.Steps.Add(new QueryPlanStep(
            source.Event,
            segment.Level switch
            {
              Level.Day => PlanSource.DayRollups,
              Level.Hour => PlanSource.HourRollups,
              Level.Minute => PlanSource.MinuteRollups,
              _ => PlanSource.RawEvents
            },
            new DateTimeOffset(segment.From, TimeSpan.Zero),
            new DateTimeOffset(segment.To, TimeSpan.Zero)));
        if (segment.Level == Level.Raw)
        {
          if (!rawBatches.TryGetValue((segment.From, segment.To), out var batch))
            rawBatches[(segment.From, segment.To)] = batch = [];
          batch.Add(source);
        }
        else
        {
          ExecuteRollup(context, source, segment);
        }
      }
    }
    foreach (var ((from, to), batch) in rawBatches)
      ExecuteRaw(context, new PlanSegment(Level.Raw, from, to), batch);
  }

  void ExecuteRollup(QueryContext context, SourceContext source, PlanSegment segment)
  {
    var definition = source.Definition;
    var dimensionCount = definition.Dimensions.Count;
    var calendar = context.Calendar;
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
    var counter = 0;
    rollups.Scan(granularity, context.Site, source.Event, definition.Id, fromBucket, toBucket,
        (bucket, combined, value) =>
        {
          if (++counter % CancellationCheckInterval == 0)
            context.CancellationToken.ThrowIfCancellationRequested();
          var parts = KeyCodec.SplitValues(combined, dimensionCount);
          for (var i = 0; i < context.Filters.Length; ++i)
          {
            if (!context.Filters[i].IsMatch(KeyCodec.Public(parts[source.FilterIndexes[i]])))
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
          GetGroup(source, outputBucket, groupValues).Merge(value);
        });
  }

  /// <summary>
  /// Returns the group of stored-form values (missing values as <see cref="KeyCodec.MissingValue"/>).
  /// </summary>
  static RollupAccumulator GetGroup(SourceContext source, long bucket, string[] storedValues)
  {
    var combined = storedValues.Length switch
    {
      0 => "",
      1 => storedValues[0],
      _ => string.Join(KeyCodec.ValueSeparator, storedValues)
    };
    var key = new GroupKey(bucket, combined);
    if (!source.Groups.TryGetValue(key, out var accumulator))
    {
      accumulator = new RollupAccumulator();
      source.Groups.Add(key, accumulator);
    }
    return accumulator;
  }

  sealed class RawSession
  {
    public long Start;

    public long Last;

    public int PageViews;

    public int Interactions;

    public ulong Visitor;

    public bool Allowed;

    public Dictionary<string, string> Dimensions;

    public bool FirstIsPageView;

    public string FirstPage;

    public string FirstHostname;

    public string LastPage;

    public string LastHostname;

    public long LastPageTicks;
  }

  void ExecuteRaw(QueryContext context, PlanSegment segment, List<SourceContext> sources)
  {
    var eventSources = sources.Where(x => x.Event != StandardEvents.Session).ToList();
    var sessionSource = sources.FirstOrDefault(x => x.Event == StandardEvents.Session);
    // Entrances and exits are only replayed when a metric reads them; like their rollups, they
    // carry page-view-level dimensions only.
    var pageViewSource = context.Metrics.Any(UsesPageFlow) &&
        context.GroupBy.Concat(context.Filters.Select(x => x.Dimension))
            .All(x => enricher.Schema.Contains(DimensionScope.PageView, x))
        ? sources.FirstOrDefault(x => x.Event == StandardEvents.PageView)
        : null;
    var trackSessions = options.Sessions.Enabled && (sessionSource != null || pageViewSource != null);
    var onlyEvent = eventSources.Count == 1 && !trackSessions ? eventSources[0] : null;

    var needed = context.GroupBy.Concat(context.Filters.Select(x => x.Dimension));
    if (trackSessions)
      needed = needed.Append(StandardDimensions.Page).Append(StandardDimensions.Hostname);
    var selector = new DimensionSelector(needed);

    var rawEvent = new RawEvent();
    var groupValues = new string[context.GroupBy.Length];
    var sessions = trackSessions ? new Dictionary<ulong, RawSession>() : null;
    var scanTo = segment.To;
    if (trackSessions)
    {
      // Session metrics are attributed to the session start and exits to the last page view.
      // Scan past the segment end to see how sessions that are open at the end continue.
      var extension = options.Sessions.Timeout.Ticks * 4;
      scanTo = Math.Max(segment.To, Math.Min(segment.To + extension, context.NowTicks + options.Ingestion.MaxFutureSkew.Ticks));
    }
    var segmentSets = context.SegmentSets;
    var counter = 0;

    rawEvents.Scan(context.Site, segment.From, scanTo, (ticks, _, value) =>
    {
      if (++counter % CancellationCheckInterval == 0)
        context.CancellationToken.ThrowIfCancellationRequested();
      var decoded = onlyEvent != null
          ? RawEventCodec.TryDecode(value.Span, ticks, onlyEvent.Event, onlyEvent.EventUtf8, rawEvent, selector)
          : RawEventCodec.TryDecode(value.Span, ticks, null, default, rawEvent, selector);
      if (!decoded)
        return true;
      var allowed = segmentSets == null || segmentSets.Allows(rawEvent.Visitor, rawEvent.SessionId);
      if (allowed && ticks < segment.To)
      {
        foreach (var source in eventSources)
        {
          if (source.Event == rawEvent.Name)
            AddRawEvent(context, source, rawEvent, groupValues);
        }
      }
      if (trackSessions && rawEvent.SessionId != 0)
        TrackSession(sessions, rawEvent, allowed);
      return true;
    });

    if (sessions == null)
      return;
    foreach (var session in sessions.Values)
    {
      if (session.Dimensions == null || !session.Allowed)
        continue;
      if (sessionSource != null && session.Start >= segment.From && session.Start < segment.To)
        AddRawSession(context, sessionSource, session, groupValues);
      if (pageViewSource != null)
        AddRawEntranceAndExit(context, pageViewSource, session, groupValues, segment);
    }
  }

  static bool UsesPageFlow(Metric metric) => metric.MetricKind switch
  {
    Metric.Kind.Entrances or Metric.Kind.Exits or Metric.Kind.ExitRate => true,
    Metric.Kind.PercentOfTotal => UsesPageFlow(metric.Inner),
    _ => false
  };

  void AddRawEvent(QueryContext context, SourceContext source, RawEvent rawEvent, string[] groupValues)
  {
    foreach (var filter in context.Filters)
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
    var accumulator = GetGroup(source, context.Calendar.Bucket(rawEvent.Ticks, context.Interval), groupValues);
    ++accumulator.Count;
    accumulator.AddVisitor(rawEvent.Visitor, context.Precision);
    foreach (var (name, value) in rawEvent.Metrics)
      accumulator.AddMetric(name, value);
  }

  void TrackSession(Dictionary<ulong, RawSession> sessions, RawEvent rawEvent, bool allowed)
  {
    if (!sessions.TryGetValue(rawEvent.SessionId, out var session))
    {
      session = new RawSession
      {
        Start = rawEvent.SessionStartTicks,
        Last = rawEvent.Ticks,
        Visitor = rawEvent.Visitor,
        Allowed = allowed
      };
      sessions.Add(rawEvent.SessionId, session);
    }
    var isFirstEvent = rawEvent.Ticks == session.Start && session.Dimensions == null;
    if (session.Dimensions == null)
    {
      var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var (key, value) in rawEvent.Dimensions)
      {
        if (key == StandardDimensions.EntryPage || enricher.Schema.IsSessionLevel(key))
          dimensions[key] = value;
      }
      session.Dimensions = dimensions;
    }
    if (rawEvent.Ticks > session.Last)
      session.Last = rawEvent.Ticks;
    if (rawEvent.Interactive)
      ++session.Interactions;
    if (rawEvent.Name != StandardEvents.PageView)
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

  void AddRawSession(QueryContext context, SourceContext source, RawSession session, string[] groupValues)
  {
    if (!MatchAndGroup(context, session.Dimensions, groupValues))
      return;
    var accumulator = GetGroup(source, context.Calendar.Bucket(session.Start, context.Interval), groupValues);
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
  void AddRawEntranceAndExit(
      QueryContext context, SourceContext source, RawSession session, string[] groupValues, PlanSegment segment)
  {
    if (session.FirstIsPageView && session.Start >= segment.From && session.Start < segment.To)
    {
      var dimensions = WithPage(session.Dimensions, session.FirstPage, session.FirstHostname);
      if (MatchAndGroup(context, dimensions, groupValues))
      {
        GetGroup(source, context.Calendar.Bucket(session.Start, context.Interval), groupValues)
            .AddMetricDelta(StandardMetrics.Entrances, 1);
      }
    }
    if (session.LastPage != null && session.LastPageTicks >= segment.From && session.LastPageTicks < segment.To)
    {
      var dimensions = WithPage(session.Dimensions, session.LastPage, session.LastHostname);
      if (MatchAndGroup(context, dimensions, groupValues))
      {
        GetGroup(source, context.Calendar.Bucket(session.LastPageTicks, context.Interval), groupValues)
            .AddMetricDelta(StandardMetrics.Exits, 1);
      }
    }
  }

  static Dictionary<string, string> WithPage(Dictionary<string, string> dimensions, string page, string hostname)
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
      if (!filter.IsMatch(dimensions.GetValueOrDefault(filter.Dimension)))
        return false;
    }
    for (var i = 0; i < groupValues.Length; ++i)
      groupValues[i] = KeyCodec.Stored(dimensions.GetValueOrDefault(context.GroupBy[i]));
    return true;
  }

  /// <summary>
  /// First pass for behavioral segments: collects the visitors and sessions with a matching
  /// event in the query range.
  /// </summary>
  SegmentSets EvaluateSegments(QueryContext context)
  {
    if (!options.Storage.StoreRawEvents || rawEvents == null)
      throw new AnalyticsQueryException("Segments require raw events (StorageOptions.StoreRawEvents).");
    CheckRawRetention(context, context.From);
    var segments = context.Segments;
    var sets = new SegmentSets
    {
      Segments = segments,
      Members = segments.Select(_ => new HashSet<ulong>()).ToArray()
    };
    var selector = new DimensionSelector(segments.SelectMany(x => x.Filters).Select(x => x.Dimension));
    var rawEvent = new RawEvent();
    var counter = 0;
    string Lookup(string dimension) => rawEvent.TryGetDimension(dimension, out var value) ? value : null;
    rawEvents.Scan(context.Site, context.From, context.To, (ticks, _, value) =>
    {
      if (++counter % CancellationCheckInterval == 0)
        context.CancellationToken.ThrowIfCancellationRequested();
      if (!RawEventCodec.TryDecode(value.Span, ticks, null, default, rawEvent, selector))
        return true;
      for (var i = 0; i < segments.Length; ++i)
      {
        var segment = segments[i];
        var id = segment.Scope == SegmentScope.Visitors ? rawEvent.Visitor : rawEvent.SessionId;
        if (id != 0 && segment.Matches(rawEvent.Name, Lookup))
          sets.Members[i].Add(id);
      }
      return true;
    });
    return sets;
  }

  #endregion

  #region Results

  (AnalyticsResult, Dictionary<AnalyticsRow, int>) BuildResult(QueryContext context, AnalyticsQuery query)
  {
    var shape = new AnalyticsResultShape(context.Metrics, context.GroupBy);
    var metrics = context.Metrics;
    var baseEvent = context.BaseEvent;

    // Totals per source: every group merged, unique visitors de-duplicated.
    var totals = new Dictionary<string, RollupAccumulator>(StringComparer.Ordinal);
    var keys = new HashSet<GroupKey>();
    var rowsFromBaseOnly = !query.IncludeRowsWithoutBaseEvent && context.Sources.ContainsKey(baseEvent);
    foreach (var source in context.Sources.Values)
    {
      var total = new RollupAccumulator();
      var addsRows = !rowsFromBaseOnly || source.Event == baseEvent;
      foreach (var (key, accumulator) in source.Groups)
      {
        if (IsEmpty(accumulator))
          continue;
        total.Merge(accumulator);
        if (addsRows)
          keys.Add(key);
      }
      totals[source.Event] = total;
    }
    var totalValues = new double[metrics.Length];
    for (var i = 0; i < metrics.Length; ++i)
    {
      if (metrics[i].MetricKind != Metric.Kind.PercentOfTotal)
        totalValues[i] = metrics[i].Evaluate(totals.GetValueOrDefault, baseEvent);
    }
    for (var i = 0; i < metrics.Length; ++i)
    {
      if (metrics[i].MetricKind == Metric.Kind.PercentOfTotal)
        totalValues[i] = Evaluate(metrics[i].Inner, totals.GetValueOrDefault, baseEvent) > 0 ? 1 : 0;
    }

    var interval = context.Interval;
    if (interval != TimeInterval.None && context.GroupBy.Length == 0 && query.FillEmptyBuckets)
    {
      foreach (var bucket in EnumerateBuckets(context.Calendar, context.From, context.To, interval))
        keys.Add(new GroupKey(bucket, ""));
    }

    var rows = new List<(AnalyticsRow Row, long Bucket)>(keys.Count);
    var innerTotals = metrics
        .Select(x => x.MetricKind == Metric.Kind.PercentOfTotal ? Evaluate(x.Inner, totals.GetValueOrDefault, baseEvent) : 0)
        .ToArray();
    foreach (var key in keys)
    {
      RollupAccumulator Source(string eventName) =>
          context.Sources.TryGetValue(eventName, out var source) ? source.Groups.GetValueOrDefault(key) : null;
      var values = new double[metrics.Length];
      for (var i = 0; i < metrics.Length; ++i)
      {
        var metric = metrics[i];
        if (metric.MetricKind == Metric.Kind.PercentOfTotal)
        {
          var inner = Evaluate(metric.Inner, Source, baseEvent);
          values[i] = innerTotals[i] > 0 ? inner / innerTotals[i] : 0;
        }
        else
        {
          values[i] = metric.Evaluate(Source, baseEvent);
        }
      }
      var dimensions = KeyCodec.SplitValues(key.Values, context.GroupBy.Length);
      for (var i = 0; i < dimensions.Length; ++i)
        dimensions[i] = KeyCodec.Public(dimensions[i]);
      var label = interval == TimeInterval.None ? (DateTimeOffset?)null : context.Calendar.BucketLabel(key.Bucket, interval);
      rows.Add((new AnalyticsRow(shape, label, dimensions, values), key.Bucket));
    }

    var orders = query.OrderBy.Where(x => x != null).ToList();
    if (orders.Count == 0)
      orders.Add(interval != TimeInterval.None ? Order.ByBucket() : Order.By(metrics[0]));
    rows.Sort((a, b) => CompareRows(a, b, orders, shape));

    var bucketIndexes = new Dictionary<AnalyticsRow, int>(ReferenceEqualityComparer.Instance);
    if (interval != TimeInterval.None)
    {
      var positions = EnumerateBuckets(context.Calendar, context.From, context.To, interval)
          .Select((bucket, index) => (bucket, index))
          .ToDictionary(x => x.bucket, x => x.index);
      foreach (var (row, bucket) in rows)
        bucketIndexes[row] = positions.TryGetValue(bucket, out var index) ? index : -1;
    }

    var totalRows = rows.Count;
    IEnumerable<AnalyticsRow> page = rows.Select(x => x.Row).Skip(query.Offset);
    if (query.Limit.HasValue)
      page = page.Take(query.Limit.Value);

    var totalsRow = new AnalyticsRow(shape, null, new string[context.GroupBy.Length], totalValues);
    var timeZone = context.Calendar.TimeZone;
    var result = new AnalyticsResult(
        shape,
        interval,
        TimeZoneInfo.ConvertTime(new DateTimeOffset(context.From, TimeSpan.Zero), timeZone),
        TimeZoneInfo.ConvertTime(new DateTimeOffset(context.To, TimeSpan.Zero), timeZone),
        timeZone,
        page.ToList(),
        totalRows,
        totalsRow,
        new QueryPlan(context.Steps, [.. context.Warnings]));
    return (result, bucketIndexes);
  }

  static double Evaluate(Metric metric, Func<string, RollupAccumulator> source, string baseEvent) =>
      metric.Evaluate(source, baseEvent);

  static bool IsEmpty(RollupAccumulator accumulator)
  {
    if (accumulator.Count != 0 || (accumulator.Visitors != null && !accumulator.Visitors.IsEmpty))
      return false;
    foreach (var value in accumulator.Metrics.Values)
    {
      if (value.Sum != 0 || value.Count != 0)
        return false;
    }
    return true;
  }

  static int CompareRows(
      (AnalyticsRow Row, long Bucket) a,
      (AnalyticsRow Row, long Bucket) b,
      List<Order> orders,
      AnalyticsResultShape shape)
  {
    foreach (var order in orders)
    {
      int result;
      if (order.IsBucket)
        result = a.Bucket.CompareTo(b.Bucket);
      else if (order.Metric != null)
        result = a.Row[order.Metric].CompareTo(b.Row[order.Metric]);
      else
      {
        var index = shape.IndexOfDimension(order.Dimension);
        result = string.CompareOrdinal(a.Row.Dimensions[index], b.Row.Dimensions[index]);
      }
      if (order.Descending)
        result = -result;
      if (result != 0)
        return result;
    }
    // Deterministic tie-breakers: bucket, then dimension values.
    var tie = a.Bucket.CompareTo(b.Bucket);
    if (tie != 0)
      return tie;
    for (var i = 0; i < a.Row.Dimensions.Count; ++i)
    {
      tie = string.CompareOrdinal(a.Row.Dimensions[i], b.Row.Dimensions[i]);
      if (tie != 0)
        return tie;
    }
    return 0;
  }

  static IEnumerable<long> EnumerateBuckets(ReportingCalendar calendar, long from, long to, TimeInterval interval)
  {
    var bucket = calendar.Bucket(from, interval);
    var isUtcInterval = interval is TimeInterval.Minute or TimeInterval.Hour;
    for (var i = 0; i <= MaxBuckets; ++i)
    {
      var start = isUtcInterval ? bucket : calendar.UtcStartOfLocalDay(bucket);
      if (start >= to)
        yield break;
      yield return bucket;
      bucket = calendar.NextBucket(bucket, interval);
    }
  }

  static void AttachComparison(
      AnalyticsResult result,
      Dictionary<AnalyticsRow, int> bucketIndexes,
      AnalyticsResult previous,
      Dictionary<AnalyticsRow, int> previousBucketIndexes)
  {
    static string Key(AnalyticsRow row, Dictionary<AnalyticsRow, int> indexes)
    {
      var index = indexes.TryGetValue(row, out var position) ? position : 0;
      return index.ToString(CultureInfo.InvariantCulture) + KeyCodec.ValueSeparator +
          string.Join(KeyCodec.ValueSeparator, row.Dimensions.Select(KeyCodec.Stored));
    }
    var lookup = new Dictionary<string, AnalyticsRow>(StringComparer.Ordinal);
    foreach (var row in previous.Rows)
      lookup.TryAdd(Key(row, previousBucketIndexes), row);
    foreach (var row in result.Rows)
      row.Previous = lookup.GetValueOrDefault(Key(row, bucketIndexes));
    result.Totals.Previous = previous.Totals;
    result.Comparison = previous;
  }

  #endregion

  #region Event names

  public IReadOnlyList<string> GetEventNames(DateRange range, string site, TimeZoneInfo timeZone)
  {
    var calendar = CalendarFor(timeZone);
    var (from, to) = range.Resolve(calendar, DateTime.UtcNow.Ticks);
    site = enricher.NormalizeSite(site);
    var names = new SortedSet<string>(StringComparer.Ordinal);
    if (rollups.IsEnabled(Granularity.Day))
    {
      names.UnionWith(rollups.ListEventNames(
          Granularity.Day, site, storageCalendar.LocalDay(from), storageCalendar.LocalDay(to) + TimeSpan.TicksPerDay));
    }
    else if (rollups.IsEnabled(Granularity.Hour))
    {
      names.UnionWith(rollups.ListEventNames(Granularity.Hour, site, from, to + TimeSpan.TicksPerHour));
    }
    else if (rollups.IsEnabled(Granularity.Minute))
    {
      names.UnionWith(rollups.ListEventNames(Granularity.Minute, site, from, to + TimeSpan.TicksPerMinute));
    }
    return [.. names];
  }

  #endregion
}
