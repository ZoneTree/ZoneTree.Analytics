using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics;

/// <summary>Full-payload ordered secondary indexes over stored event dimensions.</summary>
public sealed class DimensionIndexOptions
{
  /// <summary>
  /// Dimensions to index, compared ordinally. Empty by default. Each configured dimension
  /// stores a complete copy of every retained raw event carrying it. Existing retained events
  /// are indexed before the engine starts accepting events. Requires raw event storage.
  /// </summary>
  public List<string> Dimensions { get; private set; } = [];

  /// <summary>Combined copied records per shared index partition. Default is 10 million.</summary>
  public long RecordsPerPartition { get; set; } = PartitioningOptions.DefaultRecordsPerPartition;

  /// <summary>ZoneTree settings of the shared dimension index store and its partitions.</summary>
  public StoreOptions Store { get; private set; } = new()
  {
    MutableSegmentMaxItemCount = 50_000,
    MutableSegmentBloomFilterBitsPerItem = 0
  };

  internal DimensionIndexOptions Clone()
  {
    var clone = (DimensionIndexOptions)MemberwiseClone();
    clone.Dimensions = [.. Dimensions];
    clone.Store = Store.Clone();
    return clone;
  }

  internal void Validate(bool storeRawEvents)
  {
    if (Dimensions.Count > 0 && !storeRawEvents)
      throw new ArgumentException("Storage.DimensionIndexes requires Storage.StoreRawEvents.");
    if (RecordsPerPartition <= 0)
      throw new ArgumentException("Storage.DimensionIndexes.RecordsPerPartition must be positive.");
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var dimension in Dimensions)
    {
      Text.ValidateStoredName(dimension, "Storage.DimensionIndexes.Dimensions");
      if (dimension != dimension.Trim() || TimeDimension.IsDerived(dimension))
        throw new ArgumentException("Dimension indexes require stored dimension names without surrounding whitespace.");
      if (!names.Add(dimension))
        throw new ArgumentException($"Dimension index '{dimension}' is configured more than once.");
    }
    Store.Validate(nameof(StorageOptions.DimensionIndexes));
  }
}
