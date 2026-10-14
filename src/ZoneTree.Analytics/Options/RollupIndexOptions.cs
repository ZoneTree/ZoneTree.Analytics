using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics;

/// <summary>An equality dimension and the joint rollup copied into its ordered index.</summary>
public sealed class RollupIndexDefinition
{
  /// <summary>Creates an index over the equality dimension and the additional grouping dimensions.</summary>
  public RollupIndexDefinition(string dimension, params string[] groupDimensions)
  {
    Text.ValidateStoredName(dimension, nameof(dimension));
    ArgumentNullException.ThrowIfNull(groupDimensions);
    Dimension = dimension;
    Definition = new RollupDefinition([dimension, .. groupDimensions]);
  }

  /// <summary>The dimension placed before time in the index key.</summary>
  public string Dimension { get; }

  /// <summary>The joint dimensions. An existing matching rollup definition is reused.</summary>
  internal RollupDefinition Definition { get; }

  /// <summary>The dimensions aggregated together, including the equality dimension.</summary>
  public IReadOnlyList<string> Dimensions => Definition.Dimensions;
}

/// <summary>Optional full-key, full-value secondary indexes of explicitly selected joint rollups.</summary>
public sealed class RollupIndexOptions
{
  /// <summary>
  /// Indexed combinations. Empty by default. Missing joint rollup definitions are added to
  /// the engine's configuration copy. Retained raw events backfill newly added combinations.
  /// </summary>
  public List<RollupIndexDefinition> Definitions { get; private set; } = [];

  /// <summary>Rows per index partition. Default is ten million.</summary>
  public long RecordsPerPartition { get; set; } = PartitioningOptions.DefaultRecordsPerPartition;

  /// <summary>ZoneTree settings of the copied rollups.</summary>
  public StoreOptions Store { get; private set; } = new() { MutableSegmentMaxItemCount = 100_000 };

  internal RollupIndexOptions Clone()
  {
    var clone = (RollupIndexOptions)MemberwiseClone();
    clone.Definitions = Definitions.Select(definition => definition == null ? null :
        new RollupIndexDefinition(definition.Dimension, definition.Dimensions.Where(name => name != definition.Dimension).ToArray())).ToList();
    clone.Store = Store.Clone();
    return clone;
  }

  internal void Validate()
  {
    if (RecordsPerPartition <= 0)
      throw new ArgumentException("Storage.RollupIndexes.RecordsPerPartition must be positive.");
    var identifiers = new HashSet<string>(StringComparer.Ordinal);
    foreach (var definition in Definitions)
    {
      if (definition == null || !identifiers.Add(definition.Dimension + "\n" + definition.Definition.Id))
        throw new ArgumentException("Storage.RollupIndexes.Definitions must be non-null and distinct.");
    }
    Store.Validate(nameof(StorageOptions.RollupIndexes));
  }

  internal void AddRollups(RollupOptions rollups)
  {
    foreach (var index in Definitions)
      if (!rollups.Definitions.Any(definition => definition.Id == index.Definition.Id))
        rollups.Definitions.Add(index.Definition.Clone());
  }
}
