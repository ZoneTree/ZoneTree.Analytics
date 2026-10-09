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
  /// Default is <see cref="RollupDefinition.WebDefaults"/>.
  /// </summary>
  public List<RollupDefinition> Definitions { get; set; } = RollupDefinition.WebDefaults();

  /// <summary>
  /// Precision of unique visitor sketches. Distinct counts up to 2^precision / 4
  /// are exact; larger ones have a standard error of about 1.04 / sqrt(2^precision).
  /// It is persisted when the data directory is created and cannot be changed afterwards.
  /// Default is 14: exact up to 4,096 visitors per row, then about 0.8% error.
  /// </summary>
  public int HyperLogLogPrecision { get; set; } = HyperLogLog.DefaultPrecision;

  internal void Validate()
  {
    if ((Granularities & ~(Granularity.Minute | Granularity.Hour | Granularity.Day)) != 0)
      throw new ArgumentException("Rollups.Granularities is not a valid value.");
    HyperLogLog.ValidatePrecision(HyperLogLogPrecision);
    Definitions ??= [];
    var dimensionSets = new HashSet<string>(StringComparer.Ordinal);
    foreach (var definition in Definitions)
    {
      if (definition == null)
        throw new ArgumentException("Rollup definitions cannot contain null.");
      if (!dimensionSets.Add(definition.Id))
        throw new ArgumentException($"Duplicate rollup definition for dimensions '{definition.Id}'.");
    }
  }
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
