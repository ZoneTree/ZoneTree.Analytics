using System.Globalization;
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

  const int CancellationCheckInterval = RawEventStore.CancellationCheckInterval;

  const int MaxGroupBy = 8;

  // Time series longer than this are rejected rather than truncated.
  const int MaxBuckets = 100_000;

  // Filling time series that would create more rows than this is rejected.
  const int MaxFilledRows = 1_000_000;

  readonly AnalyticsOptions options;

  readonly ReportingCalendar storageCalendar;

  readonly RollupStore rollups;

  readonly RawEventStore rawEvents;

  readonly RollupDefinition[] definitions;

  readonly RollupActivations activations;

  readonly EnrichmentPipeline enricher;

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
    this.definitions = definitions;
    this.activations = activations;
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

    public MetricFilter[] Having;

    public FillMode Fill;

    public Dictionary<string, SourceContext> Sources = new(StringComparer.Ordinal);

    public List<QueryPlanStep> Steps = [];

    public HashSet<string> Warnings = new(StringComparer.Ordinal);

    public SegmentSets SegmentSets;

    public int Precision;

    public CancellationToken CancellationToken;

    public long NowTicks;

    public RetentionCutoffs Cutoffs;
  }

  /// <summary>
  /// A sub-query: the rows of one event, restricted by the conditions of conditional metrics.
  /// </summary>
  sealed class SourceContext
  {
    public string Event;

    /// <summary>Identifies the sub-query: the event name, plus the conditions if any.</summary>
    public string Key;

    /// <summary>The filters of conditional metrics (see <see cref="Metric.Where"/>), in addition to the query filters.</summary>
    public Filter[] Conditions = [];

    public int[] ConditionIndexes;

    /// <summary>True when entrance or exit metrics read this page view sub-query.</summary>
    public bool ReadsPageFlow;

    public byte[] EventUtf8;

    public RollupDefinition Definition;

    public int[] GroupByIndexes;

    public int[] FilterIndexes;

    public Dictionary<GroupKey, RollupAccumulator> Groups = [];

    public List<PlanSegment> Plan = [];

    /// <summary>The opt-in aggregates the query reads, by value name; null when none.</summary>
    public Dictionary<string, ValueAggregates> MetricDetails;

    /// <summary>The dimensions whose distinct values the query counts.</summary>
    public string[] DistinctDimensions = [];

    /// <summary>Since when rollups hold the opt-in aggregates the query reads.</summary>
    public long AggregatesSince;

    /// <summary>The opt-in aggregates rollups do not hold, for messages; null when they hold all.</summary>
    public string MissingAggregates;
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
    {
      if (rawEvents == null)
        throw new AnalyticsQueryException("Segments require raw events (StorageOptions.StoreRawEvents).");
      AddWarning(context, context.Cutoffs.RawEventsWarning(context.From));
      context.SegmentSets = SegmentSets.Evaluate(
          rawEvents, context.Site, context.From, context.To, context.Segments,
          options.Queries.MaxDegreeOfParallelism, context.CancellationToken);
    }
    foreach (var source in context.Sources.Values)
      Plan(context, source, query.Source);
    Execute(context);
    return BuildResult(context, query);
  }

  #endregion

  #region Preparation

  QueryContext Prepare(AnalyticsQuery query, long now, CancellationToken cancellationToken)
  {
    var baseEvent = Text.Name(query.Event);
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
          $"The range has more than {MaxBuckets.ToString("N0", CultureInfo.InvariantCulture)} {query.Interval} buckets. Use a larger interval or a shorter range.");
    }

    var groupBy = query.GroupBy
        .Select(x => x?.Trim())
        .Where(x => !string.IsNullOrEmpty(x))
        .ToArray();
    if (groupBy.Distinct(StringComparer.Ordinal).Count() != groupBy.Length)
      throw new AnalyticsQueryException("Group-by dimensions must be distinct.");
    if (groupBy.Length > MaxGroupBy)
      throw new AnalyticsQueryException($"At most {MaxGroupBy} group-by dimensions are supported.");
    var filters = query.Filters.Where(x => x != null).ToArray();
    var segments = query.Segments.Where(x => x != null).ToArray();
    var metrics = query.Metrics.Where(x => x != null).Distinct().ToArray();
    if (metrics.Length == 0)
      metrics = [Metric.Events, Metric.Visitors];
    var having = query.Having.Where(x => x != null).ToArray();
    if (!Enum.IsDefined(query.Fill))
      throw new AnalyticsQueryException("The fill mode is not a valid value.");
    // Every aggregate the query reads, including those of formulas and Having conditions.
    var aggregates = metrics.Concat(having.Select(x => x.Metric))
        .SelectMany(x => x.Aggregates())
        .Distinct()
        .ToArray();
    if (query.Interval == TimeInterval.None &&
        metrics.Concat(having.Select(x => x.Metric)).FirstOrDefault(x => x.NeedsSeries) is { } seriesMetric)
      throw new AnalyticsQueryException($"The metric '{seriesMetric.Key}' needs a time series: set an Interval.");

    var dimensions = groupBy.Concat(filters.Select(x => x.Dimension)).Distinct(StringComparer.Ordinal).ToArray();
    foreach (var metric in aggregates)
    {
      ValidateScope(metric, baseEvent, [.. dimensions.Union(metric.Conditions.Select(x => x.Dimension), StringComparer.Ordinal)]);
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
    if (query.LimitPerBucket is < 1)
      throw new AnalyticsQueryException("LimitPerBucket must be at least 1.");

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
      Having = having,
      Fill = query.Fill,
      Precision = options.Rollups.HyperLogLogPrecision,
      CancellationToken = cancellationToken,
      NowTicks = now,
      Cutoffs = RetentionCutoffs.Compute(options.Retention, storageCalendar, now)
    };

    foreach (var metric in aggregates)
    {
      foreach (var eventName in metric.Sources(baseEvent))
      {
        var key = metric.SourceKey(eventName);
        if (!context.Sources.TryGetValue(key, out var source))
        {
          source = CreateSource(eventName, key, metric.Conditions, dimensions, groupBy, filters);
          context.Sources.Add(key, source);
        }
        source.ReadsPageFlow |= metric.ReadsPageFlow;
      }
    }
    AddOptInRequirements(context, aggregates);
    return context;
  }

  /// <summary>
  /// Creates a sub-query. Its rollup definition must cover the query's group-by and filter
  /// dimensions and the dimensions of its conditions.
  /// </summary>
  SourceContext CreateSource(
      string eventName, string key, Filter[] conditions, string[] dimensions, string[] groupBy, Filter[] filters)
  {
    var dimensionSetId = string.Join(",", dimensions
        .Union(conditions.Select(x => x.Dimension), StringComparer.Ordinal)
        .Order(StringComparer.Ordinal));
    var definition = definitions.FirstOrDefault(x =>
        x.Id == dimensionSetId && (x.Events == null || x.Events.Contains(eventName)));
    var source = new SourceContext
    {
      Event = eventName,
      Key = key,
      Conditions = conditions,
      EventUtf8 = RawEventCodec.Utf8(eventName),
      Definition = definition
    };
    if (definition != null)
    {
      source.GroupByIndexes = groupBy.Select(x => IndexOf(definition.Dimensions, x)).ToArray();
      source.FilterIndexes = filters.Select(x => IndexOf(definition.Dimensions, x.Dimension)).ToArray();
      source.ConditionIndexes = conditions.Select(x => IndexOf(definition.Dimensions, x.Dimension)).ToArray();
    }
    return source;
  }

  /// <summary>
  /// Records the opt-in aggregates each source must provide (percentiles, first and last values,
  /// distinct counts) and since when rollups hold them.
  /// </summary>
  void AddOptInRequirements(QueryContext context, Metric[] aggregates)
  {
    foreach (var metric in aggregates)
    {
      var required = metric.RequiredAggregates;
      if (required == ValueAggregates.None && metric.MetricKind != Metric.Kind.Distinct)
        continue;
      var source = context.Sources[metric.SourceKey(metric.EventName ?? context.BaseEvent)];
      if (metric.MetricKind == Metric.Kind.Distinct)
      {
        if (!source.DistinctDimensions.Contains(metric.Argument))
          source.DistinctDimensions = [.. source.DistinctDimensions, metric.Argument];
        continue;
      }
      source.MetricDetails ??= new(StringComparer.Ordinal);
      source.MetricDetails[metric.Argument] = source.MetricDetails.GetValueOrDefault(metric.Argument) | required;
    }
    foreach (var source in context.Sources.Values)
    {
      long since = 0;
      var missing = new List<string>();
      foreach (var (metric, required) in source.MetricDetails ?? [])
      {
        var at = activations.AggregatesSince(metric, required);
        if (at == long.MaxValue)
          missing.Add($"{(required == ValueAggregates.FirstAndLast ? "first and last values" : "percentiles")} of '{metric}'");
        since = Math.Max(since, at);
      }
      foreach (var dimension in source.DistinctDimensions)
      {
        var at = activations.DistinctSince(dimension);
        if (at == long.MaxValue)
          missing.Add($"distinct values of '{dimension}'");
        since = Math.Max(since, at);
      }
      source.AggregatesSince = since;
      source.MissingAggregates = missing.Count == 0 ? null : string.Join(", ", missing);
    }
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
    var rawEnabled = selection != QuerySource.Rollups && rawEvents != null;
    if (context.Segments.Length > 0)
    {
      if (!rawEnabled)
        throw new AnalyticsQueryException("Segments require raw events, which are not available for this query.");
      levels.Add(Level.Raw);
    }
    else
    {
      if (source.Definition != null && selection != QuerySource.RawEvents && source.MissingAggregates == null)
      {
        var interval = context.Interval;
        if (context.DayRollupsCompatible &&
            !interval.IsFixedWidth &&
            rollups.IsEnabled(Granularity.Day))
          levels.Add(Level.Day);
        if (interval.Unit != TimeIntervalUnit.Minute && rollups.IsEnabled(Granularity.Hour))
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
      AddWarning(context, context.Cutoffs.RawEventsWarning(from));
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

  static void AddWarning(QueryContext context, string warning)
  {
    if (warning != null)
      context.Warnings.Add(warning);
  }

  static long Ceil(QueryContext context, Level level, long ticks) => level switch
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
    if (from < Math.Max(activations.DefinitionSince(source.Definition.Id), source.AggregatesSince))
      return false;
    var granularity = level switch
    {
      Level.Day => Granularity.Day,
      Level.Hour => Granularity.Hour,
      _ => Granularity.Minute
    };
    return from >= context.Cutoffs.Of(granularity);
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
          for (var i = 0; i < source.Conditions.Length; ++i)
          {
            if (!source.Conditions[i].IsMatch(KeyCodec.Public(parts[source.ConditionIndexes[i]])))
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
          GetGroup(source.Groups, outputBucket, groupValues).Merge(value);
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

    /// <summary>
    /// Appends the part of the session seen in a later slice. Every event of that part is later
    /// than the events seen so far, so the first event and the session dimensions stay, and the
    /// last page view of the part, if it has one, replaces the current one.
    /// </summary>
    public void Append(RawSession later)
    {
      Last = Math.Max(Last, later.Last);
      PageViews += later.PageViews;
      Interactions += later.Interactions;
      if (later.LastPage != null)
      {
        LastPage = later.LastPage;
        LastHostname = later.LastHostname;
        LastPageTicks = later.LastPageTicks;
      }
    }
  }

  /// <summary>
  /// The partial results of one slice of a raw scan.
  /// </summary>
  sealed class RawSlice(int eventSourceCount, int sessionShardCount, int groupByCount)
  {
    public readonly RawEvent RawEvent = new();

    /// <summary>The groups of each event source.</summary>
    public readonly Dictionary<GroupKey, RollupAccumulator>[] Groups =
        Shards.Create<Dictionary<GroupKey, RollupAccumulator>>(eventSourceCount);

    /// <summary>The sessions seen in the slice, by shard of the session id.</summary>
    public readonly Dictionary<ulong, RawSession>[] Sessions = Shards.Create<Dictionary<ulong, RawSession>>(sessionShardCount);

    public readonly string[] GroupValues = new string[groupByCount];
  }

  void ExecuteRaw(QueryContext context, PlanSegment segment, List<SourceContext> sources)
  {
    var eventSources = sources.Where(x => x.Event != StandardEvents.Session).ToArray();
    // Sessions, and entrances and exits when a metric reads them, are replayed from the events
    // of each session. Like their rollups, they carry session-level and page-view-level
    // dimensions only.
    var flowSources = sources.Where(x => x.Event == StandardEvents.Session || x.ReadsPageFlow).ToArray();
    var trackSessions = options.Sessions.Enabled && flowSources.Length > 0;
    var onlyEvent = eventSources.Length == 1 && !trackSessions ? eventSources[0] : null;

    var needed = context.GroupBy.Concat(context.Filters.Select(x => x.Dimension))
        .Concat(sources.SelectMany(x => x.Conditions.Select(c => c.Dimension)))
        .Concat(eventSources.SelectMany(x => x.DistinctDimensions));
    if (trackSessions)
      needed = needed.Append(StandardDimensions.Page).Append(StandardDimensions.Hostname);
    var selector = new DimensionSelector(needed);

    var scanTo = segment.To;
    if (trackSessions)
    {
      // Session metrics are attributed to the session start and exits to the last page view.
      // Scan past the segment end to see how sessions that are open at the end continue.
      var extension = options.Sessions.Timeout.Ticks * 4;
      scanTo = Math.Max(segment.To, Math.Min(segment.To + extension, context.NowTicks + options.Ingestion.MaxFutureSkew.Ticks));
    }
    var segmentSets = context.SegmentSets;
    var maxDegree = options.Queries.MaxDegreeOfParallelism;
    var shardCount = trackSessions ? maxDegree : 0;

    var slices = rawEvents.Scan(
        context.Site, segment.From, scanTo, maxDegree,
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
          if (allowed && ticks < segment.To)
          {
            for (var i = 0; i < eventSources.Length; ++i)
            {
              if (eventSources[i].Event == rawEvent.Name)
                AddRawEvent(context, eventSources[i], slice.Groups[i], rawEvent, slice.GroupValues);
            }
          }
          if (trackSessions && rawEvent.SessionId != 0)
            TrackSession(slice.Sessions[Shards.Of(rawEvent.SessionId, shardCount)], rawEvent, allowed);
        },
        context.CancellationToken);

    ParallelWork.For(eventSources.Length, maxDegree, i =>
    {
      foreach (var slice in slices)
        MergeGroups(eventSources[i].Groups, slice.Groups[i]);
    });
    if (!trackSessions)
      return;

    // A session can span slices. Its parts are joined in time order, shard by shard, and each
    // shard adds its complete sessions to groups of its own, per sub-query.
    var flowGroups = new Dictionary<GroupKey, RollupAccumulator>[shardCount][];
    ParallelWork.For(shardCount, maxDegree, shard =>
    {
      var groupValues = new string[context.GroupBy.Length];
      var groups = Shards.Create<Dictionary<GroupKey, RollupAccumulator>>(flowSources.Length);
      foreach (var session in JoinSessions(slices, shard).Values)
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
            AddRawEntranceAndExit(context, source, groups[i], session, groupValues, segment);
          }
        }
      }
      flowGroups[shard] = groups;
    });
    for (var i = 0; i < flowSources.Length; ++i)
    {
      for (var shard = 0; shard < shardCount; ++shard)
        MergeGroups(flowSources[i].Groups, flowGroups[shard][i]);
    }
  }

  /// <summary>
  /// Joins the parts of the sessions of one shard. The slices are in time order.
  /// </summary>
  static Dictionary<ulong, RawSession> JoinSessions(RawSlice[] slices, int shard)
  {
    if (slices.Length == 0)
      return [];
    var joined = slices[0].Sessions[shard];
    for (var i = 1; i < slices.Length; ++i)
    {
      foreach (var (id, part) in slices[i].Sessions[shard])
      {
        if (joined.TryGetValue(id, out var session))
          session.Append(part);
        else
          joined.Add(id, part);
      }
    }
    return joined;
  }

  static void AddRawEvent(
      QueryContext context,
      SourceContext source,
      Dictionary<GroupKey, RollupAccumulator> groups,
      RawEvent rawEvent,
      string[] groupValues)
  {
    foreach (var filter in context.Filters)
    {
      rawEvent.TryGetDimension(filter.Dimension, out var value);
      if (!filter.IsMatch(value))
        return;
    }
    foreach (var condition in source.Conditions)
    {
      rawEvent.TryGetDimension(condition.Dimension, out var value);
      if (!condition.IsMatch(value))
        return;
    }
    for (var i = 0; i < groupValues.Length; ++i)
    {
      rawEvent.TryGetDimension(context.GroupBy[i], out var value);
      groupValues[i] = KeyCodec.Stored(value);
    }
    var accumulator = GetGroup(groups, context.Calendar.Bucket(rawEvent.Ticks, context.Interval), groupValues);
    ++accumulator.Count;
    accumulator.AddVisitor(rawEvent.Visitor, context.Precision);
    foreach (var (name, value) in rawEvent.Values)
    {
      accumulator.AddMetric(name, value);
      if (source.MetricDetails != null && source.MetricDetails.TryGetValue(name, out var aggregates))
      {
        var bucket = (aggregates & ValueAggregates.Percentiles) != 0 ? QuantileSketch.BucketOf(value) : default;
        accumulator.AddMetricDetail(name, value, rawEvent.Ticks, aggregates, bucket);
      }
    }
    foreach (var dimension in source.DistinctDimensions)
    {
      if (rawEvent.TryGetDimension(dimension, out var value) && value != null)
        accumulator.AddDistinct(dimension, Text.StableHash(value), context.Precision);
    }
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
  static void AddRawEntranceAndExit(
      QueryContext context,
      SourceContext source,
      Dictionary<GroupKey, RollupAccumulator> groups,
      RawSession session,
      string[] groupValues,
      PlanSegment segment)
  {
    if (session.FirstIsPageView && session.Start >= segment.From && session.Start < segment.To)
    {
      var dimensions = WithPage(session.Dimensions, session.FirstPage, session.FirstHostname);
      if (MatchAndGroup(context, source, dimensions, groupValues))
      {
        GetGroup(groups, context.Calendar.Bucket(session.Start, context.Interval), groupValues)
            .AddMetricDelta(StandardMetrics.Entrances, 1);
      }
    }
    if (session.LastPage != null && session.LastPageTicks >= segment.From && session.LastPageTicks < segment.To)
    {
      var dimensions = WithPage(session.Dimensions, session.LastPage, session.LastHostname);
      if (MatchAndGroup(context, source, dimensions, groupValues))
      {
        GetGroup(groups, context.Calendar.Bucket(session.LastPageTicks, context.Interval), groupValues)
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

  static bool MatchAndGroup(
      QueryContext context, SourceContext source, Dictionary<string, string> dimensions, string[] groupValues)
  {
    foreach (var filter in context.Filters)
    {
      if (!filter.IsMatch(dimensions.GetValueOrDefault(filter.Dimension)))
        return false;
    }
    foreach (var condition in source.Conditions)
    {
      if (!condition.IsMatch(dimensions.GetValueOrDefault(condition.Dimension)))
        return false;
    }
    for (var i = 0; i < groupValues.Length; ++i)
      groupValues[i] = KeyCodec.Stored(dimensions.GetValueOrDefault(context.GroupBy[i]));
    return true;
  }

  #endregion

  #region Results

  static (AnalyticsResult, Dictionary<AnalyticsRow, int>) BuildResult(QueryContext context, AnalyticsQuery query)
  {
    var shape = new AnalyticsResultShape(context.Metrics, context.GroupBy);
    var metrics = context.Metrics;

    // Totals per source: every group merged, unique visitors de-duplicated.
    var totals = new Dictionary<string, RollupAccumulator>(StringComparer.Ordinal);
    var keys = new HashSet<GroupKey>();
    // Rows of the base event come from its unconditioned sub-query, whose key is the event name.
    var rowsFromBaseOnly = query.OnlyRowsWithBaseEvent && context.Sources.ContainsKey(context.BaseEvent);
    foreach (var source in context.Sources.Values)
    {
      var total = new RollupAccumulator();
      var addsRows = !rowsFromBaseOnly || source.Key == context.BaseEvent;
      foreach (var (key, accumulator) in source.Groups)
      {
        if (!accumulator.HasData)
          continue;
        total.Merge(accumulator);
        if (addsRows)
          keys.Add(key);
      }
      totals[source.Key] = total;
    }

    var interval = context.Interval;
    var rowKeys = keys.ToList();
    var dataRows = rowKeys.Count;
    if (interval != TimeInterval.None && context.Fill != FillMode.None)
      AddFilledRows(context, keys, rowKeys);

    // The rows of each group in bucket order, for series functions and filling.
    var series = Enumerable.Range(0, rowKeys.Count)
        .GroupBy(i => rowKeys[i].Values, StringComparer.Ordinal)
        .Select(group => group.OrderBy(i => rowKeys[i].Bucket).ToArray())
        .ToArray();
    var lookups = new Func<string, RollupAccumulator>[rowKeys.Count];
    for (var i = 0; i < dataRows; ++i)
    {
      var key = rowKeys[i];
      lookups[i] = sourceKey =>
          context.Sources.TryGetValue(sourceKey, out var source) ? source.Groups.GetValueOrDefault(key) : null;
    }
    var effectiveTo = Math.Min(context.To, Math.Max(context.From, context.NowTicks));
    var evaluator = new MetricEvaluator(
        lookups,
        totals.GetValueOrDefault,
        context.BaseEvent,
        series,
        context.Fill,
        () => RowSeconds(context, rowKeys, effectiveTo),
        (effectiveTo - context.From) / (double)TimeSpan.TicksPerSecond);
    var columns = metrics.Select(evaluator.Evaluate).ToArray();
    var having = context.Having.Select(x => (Filter: x, Values: evaluator.Evaluate(x.Metric).Rows)).ToArray();

    var rows = new List<(AnalyticsRow Row, long Bucket)>(rowKeys.Count);
    for (var r = 0; r < rowKeys.Count; ++r)
    {
      if (!Array.TrueForAll(having, x => x.Filter.IsMatch(x.Values[r])))
        continue;
      var key = rowKeys[r];
      var values = new double[metrics.Length];
      for (var m = 0; m < metrics.Length; ++m)
        values[m] = columns[m].Rows[r];
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
    if (query.LimitPerBucket is { } perBucket)
    {
      var kept = new Dictionary<long, int>();
      rows.RemoveAll(row =>
      {
        var count = kept.GetValueOrDefault(row.Bucket);
        kept[row.Bucket] = count + 1;
        return count >= perBucket;
      });
    }

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

    var totalsRow = new AnalyticsRow(
        shape, null, new string[context.GroupBy.Length], columns.Select(x => x.Total).ToArray());
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

  /// <summary>
  /// Adds a row for every bucket of the range that a group has no data for. Without group-by
  /// dimensions there is one group, so an empty range still has a row per bucket.
  /// </summary>
  static void AddFilledRows(QueryContext context, HashSet<GroupKey> keys, List<GroupKey> rowKeys)
  {
    var groups = context.GroupBy.Length == 0
        ? [""]
        : rowKeys.Select(x => x.Values).Distinct(StringComparer.Ordinal).ToList();
    var buckets = EnumerateBuckets(context.Calendar, context.From, context.To, context.Interval).ToList();
    if ((long)groups.Count * buckets.Count > MaxFilledRows)
      throw new AnalyticsQueryException(
          $"Filling the time series would create more than {MaxFilledRows.ToString("N0", CultureInfo.InvariantCulture)} rows. " +
          "Use a coarser interval, fewer groups or FillMode.None.");
    foreach (var group in groups)
    {
      foreach (var bucket in buckets)
      {
        var key = new GroupKey(bucket, group);
        if (keys.Add(key))
          rowKeys.Add(key);
      }
    }
  }

  /// <summary>
  /// The length of each row's bucket within the range, up to now, in seconds.
  /// </summary>
  static double[] RowSeconds(QueryContext context, List<GroupKey> rowKeys, long effectiveTo)
  {
    var seconds = new double[rowKeys.Count];
    var interval = context.Interval;
    for (var i = 0; i < seconds.Length; ++i)
    {
      long start = context.From, end = effectiveTo;
      if (interval != TimeInterval.None)
      {
        var bucket = rowKeys[i].Bucket;
        start = Math.Max(start, context.Calendar.UtcStartOfBucket(bucket, interval));
        end = Math.Min(end, context.Calendar.UtcStartOfBucket(ReportingCalendar.NextBucket(bucket, interval), interval));
      }
      seconds[i] = Math.Max(0, end - start) / (double)TimeSpan.TicksPerSecond;
    }
    return seconds;
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
    for (var i = 0; i <= MaxBuckets; ++i)
    {
      if (calendar.UtcStartOfBucket(bucket, interval) >= to)
        yield break;
      yield return bucket;
      bucket = ReportingCalendar.NextBucket(bucket, interval);
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
