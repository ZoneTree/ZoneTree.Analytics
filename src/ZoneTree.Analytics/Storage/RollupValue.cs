using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Serializers;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The aggregate of one measured value.
/// </summary>
/// <param name="Sum">The sum of values.</param>
/// <param name="Count">The number of values. Zero for sum-only values, which add deltas.</param>
/// <param name="Min">The smallest value.</param>
/// <param name="Max">The largest value.</param>
/// <param name="Mean">The mean, independent of a possibly overflowing sum.</param>
/// <param name="StandardDeviation">The population spread, stored without squaring it.</param>
public readonly record struct ValueStats(double Sum, long Count, double Min, double Max, double Mean, double StandardDeviation)
{
  /// <summary>
  /// The average of the values, or zero when there are none.
  /// </summary>
  public double Average => Count == 0 ? 0 : Mean;

  /// <summary>
  /// The population variance of the values, or zero when there are none.
  /// </summary>
  public double Variance => StandardDeviation * StandardDeviation;

  /// <summary>Creates statistics for one measured value.</summary>
  public static ValueStats Of(double value) => new(value, 1, value, value, value, 0);

  /// <summary>Creates a sum-only delta without a measured value count.</summary>
  public static ValueStats Delta(double delta) => new(delta, 0, 0, 0, 0, 0);

  /// <summary>
  /// Combines population moments with Chan's formula. The mean is updated independently of
  /// the sum; the spread uses a scaled norm so neither an M2 nor a squared mean difference
  /// overflows before division by the count. A genuinely unrepresentable sum or variance may
  /// still be infinite, while the mean and standard deviation remain representable.
  /// </summary>
  public static ValueStats Combine(ValueStats a, ValueStats b)
  {
    if (a.Count == 0)
      return b with { Sum = a.Sum + b.Sum, Mean = b.Count == 0 ? 0 : b.Mean + a.Sum / b.Count };
    if (b.Count == 0)
      return a with { Sum = a.Sum + b.Sum, Mean = a.Mean + b.Sum / a.Count };
    var count = a.Count + b.Count;
    var weightA = (double)a.Count / count;
    var weightB = (double)b.Count / count;
    var delta = b.Mean - a.Mean;
    var mean = double.IsFinite(delta)
        ? a.Count >= b.Count ? a.Mean + delta * weightB : b.Mean - delta * weightA
        : a.Mean * weightA + b.Mean * weightB;
    var scale = Math.Sqrt(weightA * weightB);
    var between = double.IsFinite(delta) ? delta * scale : b.Mean * scale - a.Mean * scale;
    // The standard deviation of finite doubles is at most double.MaxValue. A norm at
    // that limit may round upward to infinity, so saturate this final rounding step.
    var deviation = Math.Min(double.MaxValue, Norm(Norm(a.StandardDeviation * Math.Sqrt(weightA),
        b.StandardDeviation * Math.Sqrt(weightB)), Math.Abs(between)));
    var sum = a.Sum + b.Sum;
    if (!double.IsFinite(sum))
      sum = mean * count;
    return new ValueStats(
        sum,
        count,
        Math.Min(a.Min, b.Min),
        Math.Max(a.Max, b.Max),
        mean,
        deviation);
  }

  static double Norm(double a, double b)
  {
    if (a < b)
      (a, b) = (b, a);
    if (a == 0)
      return 0;
    var ratio = b / a;
    return a * Math.Sqrt(1 + ratio * ratio);
  }
}

/// <summary>
/// Opt-in aggregates of a measured value (see <see cref="RollupOptions.ValueAggregates"/>):
/// its first and last value, and a quantile sketch.
/// </summary>
/// <remarks>
/// Instances held by stored rows are never modified; accumulators copy them before they change.
/// Ties of first and last values at the same instant are broken by value, so results do not
/// depend on the order in which rows merge.
/// </remarks>
public sealed class ValueDetail
{
  const byte HasFirstLastFlag = 1;

  const byte HasQuantilesFlag = 2;

  /// <summary>Whether first and last values are present.</summary>
  public bool HasFirstLast;

  /// <summary>The first measured value.</summary>
  public double First;

  /// <summary>The UTC ticks of the first value.</summary>
  public long FirstTicks;

  /// <summary>The last measured value.</summary>
  public double Last;

  /// <summary>The UTC ticks of the last value.</summary>
  public long LastTicks;

  /// <summary>The quantile sketch, or null when percentiles were not collected.</summary>
  public QuantileSketch Quantiles;

  /// <summary>Includes a value in the first and last aggregates.</summary>
  public void AddFirstLast(double value, long ticks)
  {
    AddFirst(value, ticks);
    AddLast(value, ticks);
    HasFirstLast = true;
  }

  void AddFirst(double value, long ticks)
  {
    if (!HasFirstLast || ticks < FirstTicks || (ticks == FirstTicks && value < First))
    {
      First = value;
      FirstTicks = ticks;
    }
  }

  void AddLast(double value, long ticks)
  {
    if (!HasFirstLast || ticks > LastTicks || (ticks == LastTicks && value > Last))
    {
      Last = value;
      LastTicks = ticks;
    }
  }

  /// <summary>Merges the requested aggregates from another detail into this instance.</summary>
  public void MergeWith(ValueDetail other, ValueAggregates aggregates = ValueAggregates.All)
  {
    if (other.HasFirstLast && (aggregates & ValueAggregates.FirstAndLast) != 0)
    {
      AddFirst(other.First, other.FirstTicks);
      AddLast(other.Last, other.LastTicks);
      HasFirstLast = true;
    }
    if (other.Quantiles != null && (aggregates & ValueAggregates.Percentiles) != 0)
    {
      if (Quantiles == null)
        Quantiles = other.Quantiles.Clone();
      else
        Quantiles.MergeWith(other.Quantiles);
    }
  }

  /// <summary>Creates a deep copy of the requested aggregates.</summary>
  public ValueDetail Clone(ValueAggregates aggregates = ValueAggregates.All) => new()
  {
    HasFirstLast = HasFirstLast && (aggregates & ValueAggregates.FirstAndLast) != 0,
    First = First,
    FirstTicks = FirstTicks,
    Last = Last,
    LastTicks = LastTicks,
    Quantiles = (aggregates & ValueAggregates.Percentiles) != 0 ? Quantiles?.Clone() : null
  };

  /// <summary>
  /// Merges two stored details without modifying them.
  /// </summary>
  public static ValueDetail Combine(ValueDetail a, ValueDetail b)
  {
    if (a == null)
      return b;
    if (b == null)
      return a;
    var merged = a.Clone();
    merged.MergeWith(b);
    return merged;
  }

  internal void Write(ByteWriter writer)
  {
    var flags = (byte)((HasFirstLast ? HasFirstLastFlag : 0) | (Quantiles != null ? HasQuantilesFlag : 0));
    writer.WriteByte(flags);
    if (HasFirstLast)
    {
      writer.WriteDouble(First);
      writer.WriteVarInt64(FirstTicks);
      writer.WriteDouble(Last);
      writer.WriteVarInt64(LastTicks - FirstTicks);
    }
    Quantiles?.Write(writer);
  }

  internal static ValueDetail Read(ref ByteReader reader)
  {
    var flags = reader.ReadByte();
    var detail = new ValueDetail();
    if ((flags & HasFirstLastFlag) != 0)
    {
      detail.HasFirstLast = true;
      detail.First = reader.ReadDouble();
      detail.FirstTicks = reader.ReadVarInt64();
      detail.Last = reader.ReadDouble();
      detail.LastTicks = detail.FirstTicks + reader.ReadVarInt64();
    }
    if ((flags & HasQuantilesFlag) != 0)
      detail.Quantiles = QuantileSketch.Read(ref reader);
    return detail;
  }
}

/// <summary>
/// A stored rollup row. Treat instances, their arrays and sketches as immutable:
/// merges create new instances, so values held by ZoneTree segments are never modified.
/// </summary>
public sealed class RollupValue
{
  /// <summary>The number of events or session deltas represented by this row.</summary>
  public readonly long Count;

  /// <summary>The visitor sketch, or null when the row has no visitors.</summary>
  public readonly HyperLogLog Visitors;

  /// <summary>The measured value names, in ordinal order.</summary>
  public readonly string[] ValueNames;

  /// <summary>The measured statistics, aligned with <see cref="ValueNames"/>.</summary>
  public readonly ValueStats[] Values;

  // The opt-in parts, null for the rows of values without them: queries read many rows, so the
  // row itself stays small.
  readonly OptInParts optIn;

  sealed record OptInParts(ValueDetail[] Details, string[] DistinctNames, HyperLogLog[] Distinct);

  /// <summary>Creates a stored row. Arrays and sketches are retained without copying.</summary>
  public RollupValue(
      long count,
      HyperLogLog visitors,
      string[] valueNames,
      ValueStats[] values,
      ValueDetail[] details = null,
      string[] distinctNames = null,
      HyperLogLog[] distinct = null)
  {
    Count = count;
    Visitors = visitors;
    ValueNames = valueNames ?? [];
    Values = values ?? [];
    if (details != null || distinctNames is { Length: > 0 })
      optIn = new OptInParts(details, distinctNames ?? [], distinct ?? []);
  }

  /// <summary>
  /// Opt-in aggregates per value, aligned with <see cref="ValueNames"/>; null when no value has any.
  /// </summary>
  public ValueDetail[] Details => optIn?.Details;

  /// <summary>
  /// The dimensions whose distinct values are counted, in ordinal order.
  /// </summary>
  public string[] DistinctNames => optIn?.DistinctNames ?? [];

  /// <summary>
  /// Distinct value sketches, aligned with <see cref="DistinctNames"/>.
  /// </summary>
  public HyperLogLog[] Distinct => optIn?.Distinct ?? [];

  /// <summary>The opt-in detail at a value index, or null when no details were collected.</summary>
  public ValueDetail DetailAt(int index) => Details?[index];

  /// <summary>Merges two rows without modifying either row, its arrays or sketches.</summary>
  public static RollupValue Merge(RollupValue existing, RollupValue delta)
  {
    if (existing == null)
      return delta;
    if (delta == null)
      return existing;
    var visitors = Union(existing.Visitors, delta.Visitors);

    string[] names;
    ValueStats[] values;
    ValueDetail[] details;
    if (delta.ValueNames.Length == 0)
    {
      names = existing.ValueNames;
      values = existing.Values;
      details = existing.Details;
    }
    else if (existing.ValueNames.Length == 0)
    {
      names = delta.ValueNames;
      values = delta.Values;
      details = delta.Details;
    }
    else
    {
      MergeValues(existing, delta, out names, out values, out details);
    }

    string[] distinctNames;
    HyperLogLog[] distinct;
    if (delta.DistinctNames.Length == 0)
    {
      distinctNames = existing.DistinctNames;
      distinct = existing.Distinct;
    }
    else if (existing.DistinctNames.Length == 0)
    {
      distinctNames = delta.DistinctNames;
      distinct = delta.Distinct;
    }
    else
    {
      MergeDistinct(existing, delta, out distinctNames, out distinct);
    }
    return new RollupValue(existing.Count + delta.Count, visitors, names, values, details, distinctNames, distinct);
  }

  /// <summary>
  /// The union of two stored sketches, without modifying them.
  /// </summary>
  static HyperLogLog Union(HyperLogLog a, HyperLogLog b)
  {
    if (b == null || b.IsEmpty)
      return a;
    if (a == null || a.IsEmpty)
      return b;
    var union = a.Clone();
    union.UnionWith(b);
    return union;
  }

  static void MergeValues(
      RollupValue a, RollupValue b, out string[] names, out ValueStats[] values, out ValueDetail[] details)
  {
    var capacity = a.ValueNames.Length + b.ValueNames.Length;
    var resultNames = new List<string>(capacity);
    var resultValues = new List<ValueStats>(capacity);
    var resultDetails = a.Details != null || b.Details != null ? new List<ValueDetail>(capacity) : null;
    int i = 0, j = 0;
    while (i < a.ValueNames.Length || j < b.ValueNames.Length)
    {
      var cmp = i == a.ValueNames.Length ? 1 :
          j == b.ValueNames.Length ? -1 :
          string.CompareOrdinal(a.ValueNames[i], b.ValueNames[j]);
      if (cmp < 0)
      {
        resultNames.Add(a.ValueNames[i]);
        resultValues.Add(a.Values[i]);
        resultDetails?.Add(a.DetailAt(i));
        ++i;
      }
      else if (cmp > 0)
      {
        resultNames.Add(b.ValueNames[j]);
        resultValues.Add(b.Values[j]);
        resultDetails?.Add(b.DetailAt(j));
        ++j;
      }
      else
      {
        resultNames.Add(a.ValueNames[i]);
        resultValues.Add(ValueStats.Combine(a.Values[i], b.Values[j]));
        resultDetails?.Add(ValueDetail.Combine(a.DetailAt(i), b.DetailAt(j)));
        ++i;
        ++j;
      }
    }
    names = [.. resultNames];
    values = [.. resultValues];
    details = resultDetails?.ToArray();
  }

  static void MergeDistinct(RollupValue a, RollupValue b, out string[] names, out HyperLogLog[] sketches)
  {
    var resultNames = new List<string>(a.DistinctNames.Length + b.DistinctNames.Length);
    var resultSketches = new List<HyperLogLog>(resultNames.Capacity);
    int i = 0, j = 0;
    while (i < a.DistinctNames.Length || j < b.DistinctNames.Length)
    {
      var cmp = i == a.DistinctNames.Length ? 1 :
          j == b.DistinctNames.Length ? -1 :
          string.CompareOrdinal(a.DistinctNames[i], b.DistinctNames[j]);
      if (cmp < 0)
      {
        resultNames.Add(a.DistinctNames[i]);
        resultSketches.Add(a.Distinct[i++]);
      }
      else if (cmp > 0)
      {
        resultNames.Add(b.DistinctNames[j]);
        resultSketches.Add(b.Distinct[j++]);
      }
      else
      {
        resultNames.Add(a.DistinctNames[i]);
        resultSketches.Add(Union(a.Distinct[i++], b.Distinct[j++]));
      }
    }
    names = [.. resultNames];
    sketches = [.. resultSketches];
  }
}

/// <summary>
/// A mutable aggregate used by ingestion buffers and query groups.
/// </summary>
/// <remarks>
/// Rows hold few values, so they are kept in small arrays searched linearly, which allocates
/// far less than a dictionary per row. Values, sketches and details merged from stored rows are
/// shared until this accumulator needs to modify them: a query group that reads a single stored
/// row, such as one hour of an hourly series, copies nothing.
/// </remarks>
sealed class RollupAccumulator
{
  public long Count;

  HyperLogLog visitors;

  bool ownsVisitors;

  // Queries can retain visitor-only row presence without building an unused visitor sketch.
  bool hasSkippedVisitors;

  string[] valueNames;

  ValueStats[] valueStats;

  int valueCount;

  // True while the value arrays are those of a stored row, which must not be modified.
  bool sharesValues;

  // The opt-in parts, allocated only for rows that have them.
  OptInParts optIn;

  sealed class OptInParts
  {
    public readonly Dictionary<string, Shared<ValueDetail>> Details = new(StringComparer.Ordinal);

    public readonly Dictionary<string, Shared<HyperLogLog>> Distinct = new(StringComparer.Ordinal);
  }

  /// <summary>
  /// A value that may be shared with a stored row; <see cref="Owned"/> tells whether it may be modified.
  /// </summary>
  sealed class Shared<T>(T value, bool owned) where T : class
  {
    public T Value = value;

    public bool Owned = owned;
  }

  public HyperLogLog Visitors => visitors;

  /// <summary>
  /// True when the accumulator holds a count, a visitor or a value. Groups whose deltas
  /// cancel out, for example an exit that moved on to a later page, hold no data.
  /// </summary>
  public bool HasData
  {
    get
    {
      if (Count != 0 || hasSkippedVisitors || (visitors != null && !visitors.IsEmpty))
        return true;
      for (var i = 0; i < valueCount; ++i)
      {
        if (valueStats[i].Sum != 0 || valueStats[i].Count != 0)
          return true;
      }
      return false;
    }
  }

  /// <summary>
  /// The aggregates of a value; false when the row has none.
  /// </summary>
  public bool TryGetValueStats(string name, out ValueStats value)
  {
    var index = IndexOfValue(name);
    value = index < 0 ? default : valueStats[index];
    return index >= 0;
  }

  int IndexOfValue(string name)
  {
    for (var i = 0; i < valueCount; ++i)
    {
      var candidate = valueNames[i];
      if (ReferenceEquals(candidate, name) || string.Equals(candidate, name, StringComparison.Ordinal))
        return i;
    }
    return -1;
  }

  void AddValueSlot(string name, ValueStats value)
  {
    OwnValues(valueCount + 1);
    if (valueNames == null)
    {
      valueNames = new string[4];
      valueStats = new ValueStats[4];
    }
    else if (valueCount == valueNames.Length)
    {
      Array.Resize(ref valueNames, valueCount * 2);
      Array.Resize(ref valueStats, valueCount * 2);
    }
    valueNames[valueCount] = name;
    valueStats[valueCount++] = value;
  }

  /// <summary>
  /// The opt-in aggregates of a value, or null.
  /// </summary>
  public ValueDetail DetailOf(string valueName) =>
      optIn != null && optIn.Details.TryGetValue(valueName, out var detail) ? detail.Value : null;

  /// <summary>
  /// The distinct value sketch of a dimension, or null.
  /// </summary>
  public HyperLogLog DistinctOf(string dimension) =>
      optIn != null && optIn.Distinct.TryGetValue(dimension, out var sketch) ? sketch.Value : null;

  public void AddVisitor(ulong visitorHash, int precision)
  {
    if (visitorHash == 0)
      return;
    EnsureOwnVisitors(precision);
    visitors.Add(visitorHash);
  }

  void EnsureOwnVisitors(int precision)
  {
    if (visitors == null)
    {
      visitors = new HyperLogLog(precision);
      ownsVisitors = true;
    }
    else if (!ownsVisitors)
    {
      visitors = visitors.Clone();
      ownsVisitors = true;
    }
  }

  /// <summary>
  /// Records a measured value.
  /// </summary>
  public void AddValue(string name, double value) => MergeValue(name, ValueStats.Of(value));

  /// <summary>
  /// Adds a delta to a sum-only value.
  /// </summary>
  public void AddValueDelta(string name, double delta) => MergeValue(name, ValueStats.Delta(delta));

  void MergeValue(string name, ValueStats value)
  {
    var index = IndexOfValue(name);
    if (index < 0)
    {
      AddValueSlot(name, value);
      return;
    }
    OwnValues(valueCount);
    valueStats[index] = ValueStats.Combine(valueStats[index], value);
  }

  /// <summary>
  /// Replaces shared value arrays with copies that hold at least <paramref name="capacity"/> values.
  /// </summary>
  void OwnValues(int capacity)
  {
    if (!sharesValues)
      return;
    var length = Math.Max(capacity, 4);
    var names = new string[length];
    var values = new ValueStats[length];
    Array.Copy(valueNames, names, valueCount);
    Array.Copy(valueStats, values, valueCount);
    valueNames = names;
    valueStats = values;
    sharesValues = false;
  }

  /// <summary>
  /// Records the opt-in aggregates of a measured value. <paramref name="bucket"/> is the value's
  /// quantile bucket, used with <see cref="ValueAggregates.Percentiles"/>.
  /// </summary>
  public void AddValueDetail(string name, double value, long ticks, ValueAggregates aggregates, QuantileBucket bucket)
  {
    var detail = Own((optIn ??= new()).Details, name, static _ => new ValueDetail(), 0, static x => x.Clone());
    if ((aggregates & ValueAggregates.FirstAndLast) != 0)
      detail.AddFirstLast(value, ticks);
    if ((aggregates & ValueAggregates.Percentiles) != 0)
      (detail.Quantiles ??= new QuantileSketch()).Add(bucket);
  }

  /// <summary>
  /// Records a value of a dimension whose distinct values are counted, by its hash.
  /// </summary>
  public void AddDistinct(string dimension, ulong valueHash, int precision) =>
      Own((optIn ??= new()).Distinct, dimension, static precision => new HyperLogLog(precision), precision, static x => x.Clone())
          .Add(valueHash);

  /// <summary>
  /// The value of a slot that this accumulator may modify: a new one, or a copy of a shared one.
  /// </summary>
  static T Own<T>(
      Dictionary<string, Shared<T>> slots, string name, Func<int, T> create, int state, Func<T, T> copy) where T : class
  {
    if (!slots.TryGetValue(name, out var slot))
    {
      slot = new Shared<T>(create(state), true);
      slots.Add(name, slot);
    }
    else if (!slot.Owned)
    {
      slot.Value = copy(slot.Value);
      slot.Owned = true;
    }
    return slot.Value;
  }

  public void Merge(RollupValue value)
  {
    if (value == null)
      return;
    Count += value.Count;
    MergeVisitors(value.Visitors);
    MergeValues(value);
    for (var i = 0; i < value.ValueNames.Length; ++i)
    {
      if (value.DetailAt(i) is { } detail)
        MergeDetail(value.ValueNames[i], detail);
    }
    var distinctNames = value.DistinctNames;
    for (var i = 0; i < distinctNames.Length; ++i)
      MergeDistinct(distinctNames[i], value.Distinct[i]);
  }

  /// <summary>Merges only the sketches and details a query reads, preserving scalar row presence.</summary>
  public void MergeForQuery(RollupValue value, RollupReadRequirements requirements)
  {
    if (value == null)
      return;
    Count += value.Count;
    if (requirements.Visitors)
      MergeVisitors(value.Visitors);
    else
      hasSkippedVisitors |= value.Visitors != null && !value.Visitors.IsEmpty;
    MergeValues(value);
    if (requirements.ValueDetails != null)
    {
      foreach (var (name, aggregates) in requirements.ValueDetails)
      {
        var index = Array.IndexOf(value.ValueNames, name);
        if (index >= 0 && value.DetailAt(index) is { } detail)
          MergeDetail(name, detail, aggregates);
      }
    }
    foreach (var dimension in requirements.DistinctDimensions ?? [])
    {
      var index = Array.IndexOf(value.DistinctNames, dimension);
      if (index >= 0)
        MergeDistinct(dimension, value.Distinct[index]);
    }
  }

  void MergeVisitors(HyperLogLog sketch)
  {
    if (sketch == null || sketch.IsEmpty)
      return;
    if (visitors == null)
    {
      // Share the stored sketch until this accumulator needs to modify it.
      visitors = sketch;
      ownsVisitors = false;
    }
    else
    {
      EnsureOwnVisitors(visitors.Precision);
      visitors.UnionWith(sketch);
    }
  }

  void MergeValues(RollupValue value)
  {
    if (valueCount == 0 && value.ValueNames.Length > 0)
    {
      // Share the stored values until this accumulator needs to modify them.
      valueNames = value.ValueNames;
      valueStats = value.Values;
      valueCount = value.ValueNames.Length;
      sharesValues = true;
    }
    else
    {
      for (var i = 0; i < value.ValueNames.Length; ++i)
        MergeValue(value.ValueNames[i], value.Values[i]);
    }
  }

  public void Merge(RollupAccumulator other)
  {
    Count += other.Count;
    hasSkippedVisitors |= other.hasSkippedVisitors;
    if (other.visitors != null && !other.visitors.IsEmpty)
    {
      if (visitors == null)
      {
        visitors = other.visitors;
        ownsVisitors = false;
        other.ownsVisitors = false;
      }
      else
      {
        EnsureOwnVisitors(visitors.Precision);
        visitors.UnionWith(other.visitors);
      }
    }
    for (var i = 0; i < other.valueCount; ++i)
      MergeValue(other.valueNames[i], other.valueStats[i]);
    if (other.optIn == null)
      return;
    // Both accumulators may read a shared value afterwards; neither may modify it.
    foreach (var (name, slot) in other.optIn.Details)
    {
      slot.Owned = false;
      MergeDetail(name, slot.Value);
    }
    foreach (var (name, slot) in other.optIn.Distinct)
    {
      slot.Owned = false;
      MergeDistinct(name, slot.Value);
    }
  }

  /// <summary>
  /// Merges details that may be shared: a first one is shared, later ones merge into a copy.
  /// </summary>
  void MergeDetail(string name, ValueDetail detail, ValueAggregates aggregates = ValueAggregates.All)
  {
    var details = (optIn ??= new()).Details;
    if (!details.TryGetValue(name, out var slot))
    {
      var needsProjection = (detail.HasFirstLast && (aggregates & ValueAggregates.FirstAndLast) == 0) ||
          (detail.Quantiles != null && (aggregates & ValueAggregates.Percentiles) == 0);
      details.Add(name, new Shared<ValueDetail>(needsProjection ? detail.Clone(aggregates) : detail, needsProjection));
      return;
    }
    if (!slot.Owned)
    {
      slot.Value = slot.Value.Clone(aggregates);
      slot.Owned = true;
    }
    slot.Value.MergeWith(detail, aggregates);
  }

  void MergeDistinct(string dimension, HyperLogLog sketch)
  {
    if (sketch == null || sketch.IsEmpty)
      return;
    var distinct = (optIn ??= new()).Distinct;
    if (!distinct.TryGetValue(dimension, out var slot))
    {
      distinct.Add(dimension, new Shared<HyperLogLog>(sketch, false));
      return;
    }
    if (!slot.Owned)
    {
      slot.Value = slot.Value.Clone();
      slot.Owned = true;
    }
    slot.Value.UnionWith(sketch);
  }

  /// <summary>
  /// Creates an immutable value. The accumulator must not be used afterwards.
  /// </summary>
  public RollupValue ToValue()
  {
    string[] names = [];
    ValueStats[] values = [];
    ValueDetail[] valueDetails = null;
    if (valueCount > 0)
    {
      names = valueNames.AsSpan(0, valueCount).ToArray();
      values = valueStats.AsSpan(0, valueCount).ToArray();
      Array.Sort(names, values, StringComparer.Ordinal);
      if (optIn is { Details.Count: > 0 })
      {
        valueDetails = new ValueDetail[names.Length];
        for (var i = 0; i < names.Length; ++i)
          valueDetails[i] = DetailOf(names[i]);
      }
    }
    string[] distinctNames = null;
    HyperLogLog[] distinctSketches = null;
    if (optIn is { Distinct.Count: > 0 })
    {
      distinctNames = [.. optIn.Distinct.Keys];
      Array.Sort(distinctNames, StringComparer.Ordinal);
      distinctSketches = new HyperLogLog[distinctNames.Length];
      for (var i = 0; i < distinctNames.Length; ++i)
      {
        var sketch = optIn.Distinct[distinctNames[i]].Value;
        sketch.Compact();
        distinctSketches[i] = sketch;
      }
    }
    var visitorSketch = visitors == null || visitors.IsEmpty ? null : visitors;
    // Stored sketches are shared with readers on other threads: they must be compact.
    visitorSketch?.Compact();
    ownsVisitors = false;
    return new RollupValue(Count, visitorSketch, names, values, valueDetails, distinctNames, distinctSketches);
  }
}

/// <summary>
/// Binary serializer of <see cref="RollupValue"/>. The type name is part of the
/// persisted ZoneTree metadata and must stay stable.
/// </summary>
/// <remarks>
/// <code>
/// header(1: 0 = deleted, otherwise Present | HasVisitors | HasDetails | HasDistinct)
/// count(varint) [visitor sketch]
/// nameCount(varint) [name(string) sum(double) count(varint) [min(double) max(double) [mean(double) stddev(double)]]]*
/// [detail per value]            when HasDetails, see ValueDetail.Write; flags 0 for none
/// [distinctCount(varint) [dimension(string) sketch]*]   when HasDistinct
/// </code>
/// Min and max are written when count is not zero, moments when count exceeds one.
/// </remarks>
sealed class RollupValueSerializer : ISerializer<RollupValue>
{
  const byte Deleted = 0;

  const byte Present = 1;

  const byte HasVisitors = 2;

  const byte HasDetails = 4;

  const byte HasDistinct = 8;

  [ThreadStatic]
  static ByteWriter threadWriter;

  public RollupValue Deserialize(Memory<byte> bytes)
  {
    var reader = new ByteReader(bytes.Span);
    var header = reader.ReadByte();
    if (header == Deleted)
      return null;
    var count = reader.ReadVarInt64();
    var visitors = (header & HasVisitors) != 0 ? HyperLogLog.Read(ref reader) : null;
    var nameCount = (int)reader.ReadVarUInt64();
    var names = new string[nameCount];
    var values = new ValueStats[nameCount];
    for (var i = 0; i < nameCount; ++i)
    {
      names[i] = reader.ReadString();
      var sum = reader.ReadDouble();
      var valueCount = reader.ReadVarInt64();
      double min = 0, max = 0, mean = 0, deviation = 0;
      if (valueCount != 0)
      {
        min = reader.ReadDouble();
        max = reader.ReadDouble();
        mean = min;
        if (valueCount > 1)
        {
          mean = reader.ReadDouble();
          deviation = reader.ReadDouble();
        }
      }
      values[i] = new ValueStats(sum, valueCount, min, max, mean, deviation);
    }
    ValueDetail[] details = null;
    if ((header & HasDetails) != 0)
    {
      details = new ValueDetail[nameCount];
      for (var i = 0; i < nameCount; ++i)
      {
        var detail = ValueDetail.Read(ref reader);
        details[i] = detail.HasFirstLast || detail.Quantiles != null ? detail : null;
      }
    }
    string[] distinctNames = null;
    HyperLogLog[] distinct = null;
    if ((header & HasDistinct) != 0)
    {
      var distinctCount = (int)reader.ReadVarUInt64();
      distinctNames = new string[distinctCount];
      distinct = new HyperLogLog[distinctCount];
      for (var i = 0; i < distinctCount; ++i)
      {
        distinctNames[i] = reader.ReadString();
        distinct[i] = HyperLogLog.Read(ref reader);
      }
    }
    return new RollupValue(count, visitors, names, values, details, distinctNames, distinct);
  }

  public Memory<byte> Serialize(in RollupValue entry)
  {
    if (entry == null)
      return new byte[] { Deleted };
    var writer = ByteWriter.Reuse(ref threadWriter, 256);
    var hasVisitors = entry.Visitors != null && !entry.Visitors.IsEmpty;
    var hasDetails = entry.Details != null && Array.Exists(entry.Details, x => x != null);
    var hasDistinct = entry.DistinctNames.Length > 0;
    var header = Present;
    if (hasVisitors)
      header |= HasVisitors;
    if (hasDetails)
      header |= HasDetails;
    if (hasDistinct)
      header |= HasDistinct;
    writer.WriteByte(header);
    writer.WriteVarInt64(entry.Count);
    if (hasVisitors)
      entry.Visitors.Write(writer);
    writer.WriteVarUInt64((ulong)entry.ValueNames.Length);
    for (var i = 0; i < entry.ValueNames.Length; ++i)
    {
      var stats = entry.Values[i];
      writer.WriteString(entry.ValueNames[i]);
      writer.WriteDouble(stats.Sum);
      writer.WriteVarInt64(stats.Count);
      if (stats.Count != 0)
      {
        writer.WriteDouble(stats.Min);
        writer.WriteDouble(stats.Max);
        if (stats.Count > 1)
        {
          writer.WriteDouble(stats.Mean);
          writer.WriteDouble(stats.StandardDeviation);
        }
      }
    }
    if (hasDetails)
    {
      foreach (var detail in entry.Details)
      {
        if (detail == null)
          writer.WriteByte(0);
        else
          detail.Write(writer);
      }
    }
    if (hasDistinct)
    {
      writer.WriteVarUInt64((ulong)entry.DistinctNames.Length);
      for (var i = 0; i < entry.DistinctNames.Length; ++i)
      {
        writer.WriteString(entry.DistinctNames[i]);
        entry.Distinct[i].Write(writer);
      }
    }
    return writer.ToArray();
  }
}
