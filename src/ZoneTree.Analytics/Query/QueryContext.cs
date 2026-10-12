using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Query;

readonly record struct GroupKey(long Bucket, string Values);

readonly record struct RollupFilter(Filter Filter, int DimensionIndex);

/// <summary>State owned by one query execution, from preparation through result construction.</summary>
sealed class QueryContext
{
  public string Site;

  public int MaxFilledRows;

  public QueryGroupBudget GroupBudget;

  public string BaseEvent;

  public ReportingCalendar Calendar;

  public long From;

  public long To;

  public TimeInterval Interval;

  public string[] GroupBy;

  public Filter[] Filters;

  public Segment[] Segments;

  public Metric[] Metrics;

  public MetricFilter[] Having;

  public FillMode Fill;

  public readonly Dictionary<string, SourceContext> Sources = new(StringComparer.Ordinal);

  public readonly List<QueryPlanStep> Steps = [];

  public readonly HashSet<string> Warnings = new(StringComparer.Ordinal);

  public SegmentSets SegmentSets;

  public int Precision;

  public CancellationToken CancellationToken;

  public long NowTicks;

  public RetentionCutoffs Cutoffs;
}

/// <summary>A shared budget for aggregate entries across all sources and parallel slices.</summary>
sealed class QueryGroupBudget(int maximum)
{
  long count;

  public void Reserve()
  {
    if (Interlocked.Increment(ref count) > maximum)
      throw new AnalyticsQueryException(
          $"The query needs more than {maximum} aggregate groups (Queries.MaxGroups). " +
          "Use a shorter range, fewer groups or less raw-scan parallelism, or raise the limit.");
  }

  // Merging two entries into one frees a slot. Transferring an entry to another map does not.
  public void Release() => Interlocked.Decrement(ref count);
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

  /// <summary>Query filters followed by this source's conditional filters.</summary>
  public Filter[] Filters = [];

  /// <summary>True when entrance or exit metrics read this page view sub-query.</summary>
  public bool ReadsPageFlow;

  public bool ReadsVisitors;

  /// <summary>True when session measures include assigned late events before the session start.</summary>
  public bool ReadsSessionActivity;

  public byte[] EventUtf8;

  public RollupDefinition Definition;

  public int[] GroupByIndexes;

  public RollupFilter[] RollupFilters = [];

  public readonly Dictionary<GroupKey, RollupAccumulator> Groups = [];

  public List<ReadSegment> Plan = [];

  /// <summary>The opt-in aggregates the query reads, by value name; null when none.</summary>
  public Dictionary<string, ValueAggregates> ValueDetails;

  /// <summary>The dimensions whose distinct values the query counts.</summary>
  public string[] DistinctDimensions = [];

  public RollupReadRequirements ReadRequirements => new(ReadsVisitors, ValueDetails, DistinctDimensions);

  /// <summary>Since when rollups hold the opt-in aggregates the query reads.</summary>
  public long AggregatesSince;

  /// <summary>The opt-in aggregates rollups do not hold, for messages; null when they hold all.</summary>
  public string MissingAggregates;
}
