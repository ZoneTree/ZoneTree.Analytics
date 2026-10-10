using ZoneTree.Analytics.Internal;

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

  /// <summary>
  /// The text form, which <see cref="Parse"/> reads back: the metric key, the dimension or
  /// <c>bucket</c>, with a leading <c>-</c> when descending.
  /// </summary>
  public override string ToString()
  {
    var key = Metric?.Key ?? Dimension ?? "bucket";
    return Descending ? "-" + key : key;
  }

  /// <summary>
  /// Parses a sort key: <c>bucket</c>, one of <paramref name="dimensions"/> (the query's group-by
  /// dimensions), or a metric key (see <see cref="Metric.Parse"/>). A leading <c>-</c> sorts
  /// descending; otherwise the order is ascending.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
  /// <exception cref="FormatException">The text is not a sort key.</exception>
  public static Order Parse(string text, IEnumerable<string> dimensions = null) =>
      Parsing.Parse(text, "sort key", value =>
      {
        var descending = value.StartsWith('-');
        var key = (descending ? value[1..] : value).Trim();
        if (key == "bucket")
          return ByBucket(descending);
        if (dimensions != null && dimensions.Contains(key, StringComparer.Ordinal))
          return ByDimension(key, descending);
        return By(Metric.Parse(key), descending);
      });

  /// <summary>
  /// Tries to parse a sort key. See <see cref="Parse"/>.
  /// </summary>
  public static bool TryParse(string text, IEnumerable<string> dimensions, out Order order) =>
      Parsing.TryParse(text, value => Parse(value, dimensions), out order);
}
