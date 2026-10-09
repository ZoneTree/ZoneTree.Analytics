namespace ZoneTree.Analytics;

/// <summary>
/// A sort key.
/// </summary>
public sealed class Order
{
  Order(Metric metric, string dimension, bool isBucket, bool descending)
  {
    Metric = metric;
    Dimension = dimension;
    IsBucket = isBucket;
    Descending = descending;
  }

  /// <summary>The metric, for metric sort keys.</summary>
  public Metric Metric { get; }

  /// <summary>The dimension, for dimension sort keys.</summary>
  public string Dimension { get; }

  /// <summary>True for the time bucket sort key.</summary>
  public bool IsBucket { get; }

  /// <summary>True for descending order.</summary>
  public bool Descending { get; }

  /// <summary>By a metric, descending by default.</summary>
  public static Order By(Metric metric, bool descending = true)
  {
    ArgumentNullException.ThrowIfNull(metric);
    return new(metric, null, false, descending);
  }

  /// <summary>By a group-by dimension, ascending by default. Missing values sort first.</summary>
  public static Order ByDimension(string dimension, bool descending = false)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
    return new(null, dimension, false, descending);
  }

  /// <summary>By time bucket, ascending by default.</summary>
  public static Order ByBucket(bool descending = false) => new(null, null, true, descending);

  /// <inheritdoc/>
  public override string ToString()
  {
    var key = Metric?.Key ?? Dimension ?? "bucket";
    return Descending ? "-" + key : key;
  }
}
