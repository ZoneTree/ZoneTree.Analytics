using System.Globalization;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

/// <summary>Validates a query and resolves the sources, dimensions and aggregates it needs.</summary>
sealed class QueryPreparation(
    AnalyticsOptions options, ReportingCalendar storageCalendar, RollupDefinition[] definitions,
    RollupActivations activations, EnrichmentPipeline enricher)
{
  public ReportingCalendar CalendarFor(TimeZoneInfo timeZone)
  {
    if (timeZone == null || timeZone.Id == storageCalendar.TimeZone.Id)
      return storageCalendar;
    return new ReportingCalendar(timeZone, options.FirstDayOfWeek);
  }

  public QueryContext Prepare(AnalyticsQuery query, long now, CancellationToken cancellationToken)
  {
    var baseEvent = Text.Name(query.Event);
    if (baseEvent.Length == 0)
      throw new AnalyticsQueryException("The query event is required.");
    var calendar = CalendarFor(query.TimeZone);
    var (from, to) = query.Range.Resolve(calendar, now);
    if (from >= to)
      throw new AnalyticsQueryException("The query range is empty.");
    var limits = options.Queries;
    if (query.Interval != TimeInterval.None &&
        calendar.EnumerateBuckets(from, to, query.Interval).Skip(limits.MaxTimeBuckets).Any())
    {
      throw new AnalyticsQueryException(
          $"The range has more than {limits.MaxTimeBuckets.ToString("N0", CultureInfo.InvariantCulture)} {query.Interval} buckets " +
          "(Queries.MaxTimeBuckets). Use a larger interval or a shorter range.");
    }

    var groupBy = query.GroupBy
        .Select(x => x?.Trim())
        .Where(x => !string.IsNullOrEmpty(x))
        .ToArray();
    if (groupBy.Distinct(StringComparer.Ordinal).Count() != groupBy.Length)
      throw new AnalyticsQueryException("Group-by dimensions must be distinct.");
    if (groupBy.Length > limits.MaxGroupBy)
      throw new AnalyticsQueryException($"At most {limits.MaxGroupBy} group-by dimensions are supported (Queries.MaxGroupBy).");
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

    var dimensions = groupBy.Concat(filters.SelectMany(x => x.Dimensions)).Distinct(StringComparer.Ordinal).ToArray();
    foreach (var metric in aggregates)
    {
      ValidateScope(metric, baseEvent, [.. dimensions.Union(metric.Conditions.SelectMany(x => x.Dimensions), StringComparer.Ordinal)]);
      if (metric.MetricKind == Metric.Kind.Distinct && metric.ScopeFor(baseEvent) == DimensionScope.Session)
        ValidateScope(metric, baseEvent, [metric.Argument]);
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
      From = from,
      To = to,
      Interval = query.Interval,
      GroupBy = groupBy,
      Filters = filters,
      Segments = segments,
      Metrics = metrics,
      Having = having,
      Fill = query.Fill,
      MaxFilledRows = limits.MaxFilledRows,
      GroupBudget = new QueryGroupBudget(limits.MaxGroups),
      Precision = options.Rollups.HyperLogLogPrecision,
      CancellationToken = cancellationToken,
      NowTicks = now,
      Cutoffs = RetentionCutoffs.Compute(options.Retention, storageCalendar, now),
      UseDimensionIndexes = query.Source == QuerySource.Auto
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
        source.ReadsVisitors |= metric.MetricKind is Metric.Kind.Visitors or Metric.Kind.ConversionRate;
        source.ReadsSessionActivity |= eventName == StandardEvents.Session &&
            (metric.MetricKind is Metric.Kind.Bounces or Metric.Kind.BounceRate or Metric.Kind.PageViewsPerSession ||
             metric.MetricKind == Metric.Kind.Sum && metric.Argument is StandardValues.Bounces or StandardValues.SessionPageViews);
      }
    }
    if (query.OnlyRowsWithBaseEvent && !context.Sources.ContainsKey(baseEvent))
    {
      ValidateScope(Metric.Events, baseEvent, dimensions);
      context.Sources.Add(baseEvent, CreateSource(baseEvent, baseEvent, [], dimensions, groupBy, filters));
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
    var required = dimensions.Union(conditions.SelectMany(x => x.Dimensions), StringComparer.Ordinal).ToArray();
    var stored = required.Where(x => !TimeDimension.IsDerived(x)).Order(StringComparer.Ordinal).ToArray();
    var dimensionSetId = string.Join(",", stored);
    var definition = definitions.FirstOrDefault(x =>
        x.Id == dimensionSetId && (x.Events == null || x.Events.Contains(eventName)));
    var source = new SourceContext
    {
      Event = eventName,
      Key = key,
      Conditions = conditions,
      Filters = conditions.Length == 0 ? filters : filters.Length == 0 ? conditions : [.. filters, .. conditions],
      EventUtf8 = RawEventCodec.Utf8(eventName),
      RequiredDimensions = stored,
      TimeDimensions = required.Where(TimeDimension.IsDerived).ToArray(),
      Definition = definition
    };
    if (definition != null)
    {
      source.GroupByIndexes = groupBy.Select(x => IndexOf(definition.Dimensions, x)).ToArray();
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
        if (TimeDimension.IsDerived(metric.Argument) && !source.TimeDimensions.Contains(metric.Argument))
          source.TimeDimensions = [.. source.TimeDimensions, metric.Argument];
        continue;
      }
      source.ValueDetails ??= new(StringComparer.Ordinal);
      source.ValueDetails[metric.Argument] = source.ValueDetails.GetValueOrDefault(metric.Argument) | required;
    }
    foreach (var source in context.Sources.Values)
    {
      source.StoredDistinctDimensions = source.DistinctDimensions.Where(x => !TimeDimension.IsDerived(x)).ToArray();
      long since = 0;
      var missing = new List<string>();
      foreach (var (metric, required) in source.ValueDetails ?? [])
      {
        var at = activations.AggregatesSince(metric, required);
        if (at == long.MaxValue)
          missing.Add($"{(required == ValueAggregates.FirstAndLast ? "first and last values" : "percentiles")} of '{metric}'");
        since = Math.Max(since, at);
      }
      foreach (var dimension in source.DistinctDimensions)
      {
        if (TimeDimension.IsDerived(dimension))
          continue;
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
      if (TimeDimension.IsDerived(dimension) || enricher.Schema.Contains(scope, dimension))
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

}
