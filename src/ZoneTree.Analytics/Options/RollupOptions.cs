using ZoneTree.Analytics.Sketches;

namespace ZoneTree.Analytics;

/// <summary>
/// Pre-aggregation options.
/// </summary>
public sealed class RollupOptions
{
  /// <summary>
  /// Rollup granularities. Default is <see cref="Granularity.Hour"/> | <see cref="Granularity.Day"/>.
  /// Week, month and year intervals are computed from daily rollups.
  /// </summary>
  public Granularity Granularities { get; set; } = Granularity.Hour | Granularity.Day;

  /// <summary>
  /// The dimension sets to pre-aggregate. A query is answered from rollups when the
  /// union of its group-by and filter dimensions equals one of these sets;
  /// otherwise it is answered by scanning raw events.
  /// Default is <see cref="RollupDefinition.WebDefaults"/>; clear the list to start from scratch.
  /// </summary>
  public List<RollupDefinition> Definitions { get; private set; } = RollupDefinition.WebDefaults();

  /// <summary>
  /// Precision of unique visitor sketches. Distinct counts up to 2^precision / 4
  /// are exact; larger ones have a standard error of about 1.04 / sqrt(2^precision).
  /// It is persisted when the data directory is created and cannot be changed afterwards.
  /// Default is 14: exact up to 4,096 visitors per row, then about 0.8% error.
  /// </summary>
  public int HyperLogLogPrecision { get; set; } = HyperLogLog.DefaultPrecision;

  /// <summary>
  /// Additional aggregates kept in rollups for values tracked on events
  /// (<see cref="AnalyticsEvent.Values"/>), by value name. Every value has sum, count, min, max,
  /// average and variance; these are opt-in because they cost storage in every rollup row the
  /// value reaches:
  /// <see cref="Analytics.ValueAggregates.Percentiles"/> for <see cref="Metric.Percentile"/>
  /// and <see cref="Metric.Median"/>, <see cref="Analytics.ValueAggregates.FirstAndLast"/> for
  /// <see cref="Metric.First"/> and <see cref="Metric.Last"/>. Queries of a value without them,
  /// and of ranges before they were configured, are answered from raw events.
  /// </summary>
  /// <example>
  /// <code>
  /// options.Rollups.ValueAggregates["load_time"] = ValueAggregates.Percentiles;
  /// options.Rollups.ValueAggregates["temperature"] = ValueAggregates.FirstAndLast;
  /// </code>
  /// </example>
  public Dictionary<string, ValueAggregates> ValueAggregates { get; private set; } = new(StringComparer.Ordinal);

  /// <summary>
  /// Dimensions whose distinct values are counted in every rollup row, for
  /// <see cref="Metric.Distinct"/>, for example a tenant or product id. Each costs a distinct
  /// count sketch per rollup row. Queries of other dimensions, and of ranges before a dimension
  /// was configured, are answered from raw events.
  /// </summary>
  public List<string> DistinctDimensions { get; private set; } = [];

  internal RollupOptions Clone()
  {
    var clone = (RollupOptions)MemberwiseClone();
    clone.Definitions = Definitions.Select(x => x?.Clone()).ToList();
    clone.ValueAggregates = new(ValueAggregates, StringComparer.Ordinal);
    clone.DistinctDimensions = [.. DistinctDimensions];
    return clone;
  }

  internal void Validate()
  {
    if ((Granularities & ~(Granularity.Minute | Granularity.Hour | Granularity.Day)) != 0)
      throw new ArgumentException("Rollups.Granularities is not a valid value.");
    HyperLogLog.ValidatePrecision(HyperLogLogPrecision);
    var dimensionSets = new HashSet<string>(StringComparer.Ordinal);
    foreach (var definition in Definitions)
    {
      if (definition == null)
        throw new ArgumentException("Rollups.Definitions cannot contain null.");
      if (!dimensionSets.Add(definition.Id))
        throw new ArgumentException($"Rollups.Definitions contains two definitions of the dimensions '{definition.Id}'.");
    }
    foreach (var (value, aggregates) in ValueAggregates)
    {
      if (string.IsNullOrWhiteSpace(value))
        throw new ArgumentException("Rollups.ValueAggregates cannot contain an empty value name.");
      if ((aggregates & ~Analytics.ValueAggregates.All) != 0)
        throw new ArgumentException($"Rollups.ValueAggregates['{value}'] is not a valid combination.");
    }
    if (DistinctDimensions.Any(string.IsNullOrWhiteSpace))
      throw new ArgumentException("Rollups.DistinctDimensions cannot contain an empty dimension.");
    if (DistinctDimensions.Distinct(StringComparer.Ordinal).Count() != DistinctDimensions.Count)
      throw new ArgumentException("Rollups.DistinctDimensions cannot contain duplicates.");
  }
}

/// <summary>
/// Opt-in aggregates of a tracked value. See <see cref="RollupOptions.ValueAggregates"/>.
/// </summary>
[Flags]
public enum ValueAggregates
{
  /// <summary>Only the aggregates every value has: sum, count, min, max, average and variance.</summary>
  None = 0,

  /// <summary>
  /// A quantile sketch per rollup row, for <see cref="Metric.Percentile"/> and
  /// <see cref="Metric.Median"/>, with the relative-error guarantee and range limitations
  /// described by <see cref="Metric.Percentile"/>.
  /// </summary>
  Percentiles = 1,

  /// <summary>The first and last value per rollup row, for <see cref="Metric.First"/> and <see cref="Metric.Last"/>: gauges.</summary>
  FirstAndLast = 2,

  /// <summary>Every aggregate.</summary>
  All = Percentiles | FirstAndLast,
}

/// <summary>
/// A pre-aggregated dimension set.
/// </summary>
public sealed class RollupDefinition
{
  /// <summary>
  /// Creates a rollup over the given dimensions. No dimensions means totals.
  /// </summary>
  public RollupDefinition(params string[] dimensions)
  {
    dimensions ??= [];
    var sorted = dimensions
        .Select(x => x?.Trim())
        .Where(x => !string.IsNullOrEmpty(x))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();
    if (sorted.Length != dimensions.Length)
      throw new ArgumentException("Rollup dimensions must be distinct and non-empty.");
    foreach (var dimension in sorted)
    {
      if (Internal.TimeDimension.IsDerived(dimension))
        throw new ArgumentException($"'{dimension}' is computed at query time; omit it from RollupDefinition dimensions.");
      if (dimension.Any(c => char.IsControl(c) || c == ','))
        throw new ArgumentException($"Invalid dimension name '{dimension}'.");
    }
    Dimensions = sorted;
    Id = string.Join(",", sorted);
  }

  /// <summary>
  /// The dimensions in canonical (ordinal) order.
  /// </summary>
  public IReadOnlyList<string> Dimensions { get; }

  /// <summary>
  /// The canonical identifier: the dimension names joined by commas.
  /// </summary>
  public string Id { get; }

  /// <summary>
  /// Restricts the rollup to the given event names. Null means every tracked event; events
  /// that do not carry a dimension are counted under a missing (null) value.
  /// </summary>
  public IReadOnlyCollection<string> Events { get; init; }

  /// <summary>
  /// A copy that does not share the event list, which callers may still modify.
  /// </summary>
  internal RollupDefinition Clone() => new([.. Dimensions]) { Events = Events?.ToArray() };

  /// <summary>
  /// The default web analytics rollups: totals, page, hostname, entry page, every
  /// session-level dimension except browser and OS versions, and bot name and category
  /// for bot events.
  /// </summary>
  public static List<RollupDefinition> WebDefaults()
  {
    var list = new List<RollupDefinition>
    {
      new(),
      new(StandardDimensions.Page),
      new(StandardDimensions.Hostname),
      new(StandardDimensions.EntryPage),
      new(StandardDimensions.Bot) { Events = [StandardEvents.Bot] },
      new(StandardDimensions.BotCategory) { Events = [StandardEvents.Bot] },
      new(StandardDimensions.Event) { Events = [StandardEvents.Bot] },
      new(StandardDimensions.Bot, StandardDimensions.BotCategory) { Events = [StandardEvents.Bot] },
    };
    foreach (var dimension in StandardDimensions.SessionLevel)
    {
      if (dimension == StandardDimensions.BrowserVersion ||
          dimension == StandardDimensions.OperatingSystemVersion)
        continue;
      list.Add(new(dimension));
    }
    return list;
  }

  /// <inheritdoc/>
  public override string ToString() => Id.Length == 0 ? "(totals)" : Id;
}
