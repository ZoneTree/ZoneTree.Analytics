using System.Runtime.InteropServices;
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
/// A part of a stored row that is kept as its stored bytes until it is read. Concurrent
/// readers share one decoded instance.
/// </summary>
sealed class RollupSection<T> where T : class
{
  readonly Func<ReadOnlyMemory<byte>, T> decode;

  T value;

  /// <summary>A section created from a value.</summary>
  public RollupSection(T value) => this.value = value;

  /// <summary>A stored section, decoded on first read.</summary>
  public RollupSection(ReadOnlyMemory<byte> bytes, Func<ReadOnlyMemory<byte>, T> decode)
  {
    Bytes = bytes;
    this.decode = decode;
  }

  /// <summary>The stored encoding; empty for a section created from a value.</summary>
  public ReadOnlyMemory<byte> Bytes { get; }

  public bool IsStored => !Bytes.IsEmpty;

  public T Value
  {
    get
    {
      var current = Volatile.Read(ref value);
      if (current != null)
        return current;
      var decoded = decode(Bytes);
      return Interlocked.CompareExchange(ref value, decoded, null) ?? decoded;
    }
  }
}

/// <summary>
/// The value details of a row, aligned with its value names: one entry per value, null for a
/// value without details. Each entry stays in its stored bytes until it is read.
/// </summary>
sealed class DetailEntries(RollupSection<ValueDetail>[] entries)
{
  public readonly RollupSection<ValueDetail>[] Entries = entries;

  ValueDetail[] all;

  public bool HasAny => Array.Exists(Entries, x => x != null);

  /// <summary>The detail of a value, decoding only that detail.</summary>
  public ValueDetail At(int index) => Entries[index]?.Value;

  /// <summary>Every detail, decoded once.</summary>
  public ValueDetail[] All
  {
    get
    {
      var current = Volatile.Read(ref all);
      if (current != null)
        return current;
      var decoded = Array.ConvertAll(Entries, x => x?.Value);
      return Interlocked.CompareExchange(ref all, decoded, null) ?? decoded;
    }
  }

  /// <summary>The entries of details created in memory; a detail without data has none.</summary>
  public static DetailEntries Of(ValueDetail[] details) => new(Array.ConvertAll(details,
      x => x != null && (x.HasFirstLast || x.Quantiles != null) ? new RollupSection<ValueDetail>(x) : null));
}

/// <summary>
/// The distinct-value sketches of a row, by dimension in ordinal order. Each sketch stays in its
/// stored bytes until it is read.
/// </summary>
sealed class DistinctEntries(string[] names, RollupSection<HyperLogLog>[] sketches)
{
  public readonly string[] Names = names;

  public readonly RollupSection<HyperLogLog>[] Sketches = sketches;

  HyperLogLog[] all;

  /// <summary>Every sketch, decoded once.</summary>
  public HyperLogLog[] All
  {
    get
    {
      var current = Volatile.Read(ref all);
      if (current != null)
        return current;
      var decoded = Array.ConvertAll(Sketches, x => x.Value);
      return Interlocked.CompareExchange(ref all, decoded, null) ?? decoded;
    }
  }

  /// <summary>The entries of sketches created in memory.</summary>
  public static DistinctEntries Of(string[] names, HyperLogLog[] sketches) =>
      new(names, Array.ConvertAll(sketches, x => new RollupSection<HyperLogLog>(x)));
}

/// <summary>
/// A stored rollup row. Treat instances, their arrays and sketches as immutable:
/// merges create new instances, so values held by ZoneTree segments are never modified.
/// </summary>
/// <remarks>
/// The count and the scalar statistics are decoded with the row. The visitor sketch, each value
/// detail and each distinct sketch stay in their stored bytes until they are read: most queries
/// need few of them, and merges or index copies that leave them unchanged write the stored bytes
/// back without decoding them.
/// </remarks>
public sealed class RollupValue
{
  /// <summary>The number of events or session deltas represented by this row.</summary>
  public readonly long Count;

  /// <summary>The measured value names, in ordinal order.</summary>
  public readonly string[] ValueNames;

  /// <summary>The measured statistics, aligned with <see cref="ValueNames"/>.</summary>
  public readonly ValueStats[] Values;

  // The sections, null for rows without them: queries read many rows, so the row stays small.
  readonly RollupSection<HyperLogLog> visitors;

  readonly RollupSection<DetailEntries> details;

  readonly RollupSection<DistinctEntries> distinct;

  /// <summary>Creates a stored row. Arrays and sketches are retained without copying.</summary>
  public RollupValue(
      long count,
      HyperLogLog visitors,
      string[] valueNames,
      ValueStats[] values,
      ValueDetail[] details = null,
      string[] distinctNames = null,
      HyperLogLog[] distinct = null)
      : this(
          count,
          visitors == null ? null : new(visitors),
          valueNames,
          values,
          details == null ? null : new(DetailEntries.Of(details)),
          distinctNames is { Length: > 0 } ? new(DistinctEntries.Of(distinctNames, distinct ?? [])) : null)
  {
  }

  internal RollupValue(
      long count,
      RollupSection<HyperLogLog> visitors,
      string[] valueNames,
      ValueStats[] values,
      RollupSection<DetailEntries> details,
      RollupSection<DistinctEntries> distinct)
  {
    Count = count;
    ValueNames = valueNames ?? [];
    Values = values ?? [];
    this.visitors = visitors;
    this.details = details;
    this.distinct = distinct;
  }

  /// <summary>The visitor sketch, or null when the row has no visitors.</summary>
  public HyperLogLog Visitors => visitors?.Value;

  /// <summary>Whether the row has a visitor sketch with visitors, without decoding it.</summary>
  public bool HasVisitors => visitors != null && (visitors.IsStored || !visitors.Value.IsEmpty);

  /// <summary>
  /// Opt-in aggregates per value, aligned with <see cref="ValueNames"/>; null when no value has any.
  /// Decodes every detail; <see cref="DetailAt"/> decodes one.
  /// </summary>
  public ValueDetail[] Details => details?.Value.All;

  /// <summary>
  /// The dimensions whose distinct values are counted, in ordinal order.
  /// </summary>
  public string[] DistinctNames => distinct?.Value.Names ?? [];

  /// <summary>
  /// Distinct value sketches, aligned with <see cref="DistinctNames"/>. Decodes every sketch;
  /// <see cref="DistinctAt"/> decodes one.
  /// </summary>
  public HyperLogLog[] Distinct => distinct?.Value.All ?? [];

  /// <summary>The opt-in detail at a value index, or null when no details were collected.</summary>
  public ValueDetail DetailAt(int index) => details?.Value.At(index);

  /// <summary>The distinct value sketch at an index of <see cref="DistinctNames"/>.</summary>
  public HyperLogLog DistinctAt(int index) => distinct.Value.Sketches[index].Value;

  internal RollupSection<HyperLogLog> VisitorSection => HasVisitors ? visitors : null;

  internal RollupSection<DetailEntries> DetailSection =>
      details != null && (details.IsStored || details.Value.HasAny) ? details : null;

  internal RollupSection<DistinctEntries> DistinctSection =>
      distinct != null && (distinct.IsStored || distinct.Value.Names.Length > 0) ? distinct : null;

  internal RollupSection<ValueDetail> DetailEntryAt(int index) => details?.Value.Entries[index];

  internal RollupSection<HyperLogLog> DistinctEntryAt(int index) => distinct.Value.Sketches[index];

  /// <summary>Merges two rows without modifying either row, its arrays or sketches.</summary>
  /// <remarks>
  /// A section, detail or distinct sketch only one row has is shared, so its stored bytes are
  /// written back as they are.
  /// </remarks>
  public static RollupValue Merge(RollupValue existing, RollupValue delta)
  {
    if (existing == null)
      return delta;
    if (delta == null)
      return existing;
    var visitors = !delta.HasVisitors ? existing.visitors :
        !existing.HasVisitors ? delta.visitors :
        new RollupSection<HyperLogLog>(Union(existing.Visitors, delta.Visitors));

    string[] names;
    ValueStats[] values;
    RollupSection<DetailEntries> details;
    if (delta.ValueNames.Length == 0)
    {
      names = existing.ValueNames;
      values = existing.Values;
      details = existing.details;
    }
    else if (existing.ValueNames.Length == 0)
    {
      names = delta.ValueNames;
      values = delta.Values;
      details = delta.details;
    }
    else
    {
      MergeValues(existing, delta, out names, out values, out details);
    }

    var distinct = delta.DistinctSection == null ? existing.distinct :
        existing.DistinctSection == null ? delta.distinct :
        new RollupSection<DistinctEntries>(MergeDistinct(existing, delta));
    return new RollupValue(existing.Count + delta.Count, visitors, names, values, details, distinct);
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

  /// <summary>The entry of two merged details: one row's entry as it is, or the merged details.</summary>
  static RollupSection<ValueDetail> MergeEntries(RollupSection<ValueDetail> a, RollupSection<ValueDetail> b) =>
      a == null ? b : b == null ? a : new(ValueDetail.Combine(a.Value, b.Value));

  static void MergeValues(
      RollupValue a, RollupValue b, out string[] names, out ValueStats[] values, out RollupSection<DetailEntries> details)
  {
    // Rows of the same values keep the details of the only row that has them.
    if (a.ValueNames.AsSpan().SequenceEqual(b.ValueNames) && (a.details == null || b.details == null))
    {
      names = a.ValueNames;
      values = new ValueStats[names.Length];
      for (var k = 0; k < values.Length; ++k)
        values[k] = ValueStats.Combine(a.Values[k], b.Values[k]);
      details = a.details ?? b.details;
      return;
    }
    var capacity = a.ValueNames.Length + b.ValueNames.Length;
    var resultNames = new List<string>(capacity);
    var resultValues = new List<ValueStats>(capacity);
    var resultDetails = a.details != null || b.details != null ? new List<RollupSection<ValueDetail>>(capacity) : null;
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
        resultDetails?.Add(a.DetailEntryAt(i));
        ++i;
      }
      else if (cmp > 0)
      {
        resultNames.Add(b.ValueNames[j]);
        resultValues.Add(b.Values[j]);
        resultDetails?.Add(b.DetailEntryAt(j));
        ++j;
      }
      else
      {
        resultNames.Add(a.ValueNames[i]);
        resultValues.Add(ValueStats.Combine(a.Values[i], b.Values[j]));
        resultDetails?.Add(MergeEntries(a.DetailEntryAt(i), b.DetailEntryAt(j)));
        ++i;
        ++j;
      }
    }
    names = [.. resultNames];
    values = [.. resultValues];
    details = resultDetails == null ? null : new(new DetailEntries([.. resultDetails]));
  }

  static DistinctEntries MergeDistinct(RollupValue a, RollupValue b)
  {
    var aNames = a.DistinctNames;
    var bNames = b.DistinctNames;
    var resultNames = new List<string>(aNames.Length + bNames.Length);
    var resultSketches = new List<RollupSection<HyperLogLog>>(resultNames.Capacity);
    int i = 0, j = 0;
    while (i < aNames.Length || j < bNames.Length)
    {
      var cmp = i == aNames.Length ? 1 :
          j == bNames.Length ? -1 :
          string.CompareOrdinal(aNames[i], bNames[j]);
      if (cmp < 0)
      {
        resultNames.Add(aNames[i]);
        resultSketches.Add(a.DistinctEntryAt(i++));
      }
      else if (cmp > 0)
      {
        resultNames.Add(bNames[j]);
        resultSketches.Add(b.DistinctEntryAt(j++));
      }
      else
      {
        resultNames.Add(aNames[i]);
        var stored = a.DistinctEntryAt(i++);
        var union = Union(stored.Value, b.DistinctAt(j++));
        // A union that adds nothing keeps the stored entry and its bytes.
        resultSketches.Add(ReferenceEquals(union, stored.Value) ? stored : new(union));
      }
    }
    return new DistinctEntries([.. resultNames], [.. resultSketches]);
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
      MergeDistinct(distinctNames[i], value.DistinctAt(i));
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
      hasSkippedVisitors |= value.HasVisitors;
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
        MergeDistinct(dimension, value.DistinctAt(index));
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

  /// <summary>
  /// Merges <paramref name="accumulator"/> into the row of <paramref name="key"/>, or adds it as
  /// that row. Returns true when it merged into an existing row.
  /// </summary>
  public static bool MergeInto<TKey>(Dictionary<TKey, RollupAccumulator> rows, TKey key, RollupAccumulator accumulator)
  {
    ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(rows, key, out var exists);
    if (!exists)
    {
      existing = accumulator;
      return false;
    }
    existing.Merge(accumulator);
    return true;
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
/// count(varint)
/// nameCount(varint) [name(string) sum(double) count(varint) [min(double) max(double) [mean(double) stddev(double)]]]*
/// [length(varint) visitor sketch]                                                    when HasVisitors
/// [length(varint) detailCount(varint) [length(varint) detail]*]                      when HasDetails; length 0 for none
/// [length(varint) distinctCount(varint) [dimension(string) length(varint) sketch]*]  when HasDistinct
/// </code>
/// Min and max are written when count is not zero, moments when count exceeds one. Sections,
/// details and distinct sketches are length-prefixed and decoded only when read, one at a time.
/// A reader rejects header bits it does not know and trailing bytes.
/// </remarks>
sealed class RollupValueSerializer : ISerializer<RollupValue>
{
  const byte Deleted = 0;

  const byte Present = 1;

  const byte HasVisitors = 2;

  const byte HasDetails = 4;

  const byte HasDistinct = 8;

  const byte KnownBits = Present | HasVisitors | HasDetails | HasDistinct;

  delegate T SectionReader<T>(ref ByteReader reader);

  static readonly Func<ReadOnlyMemory<byte>, HyperLogLog> DecodeVisitors =
      bytes => Decode(bytes, static (ref ByteReader reader) => HyperLogLog.Read(ref reader));

  static readonly Func<ReadOnlyMemory<byte>, ValueDetail> DecodeDetail = bytes => Decode(bytes, static (ref ByteReader reader) =>
  {
    var detail = ValueDetail.Read(ref reader);
    // A value without details has no entry: a stored detail always holds data.
    if (!detail.HasFirstLast && detail.Quantiles == null)
      throw new FormatException("A stored value detail is empty.");
    return detail;
  });

  static readonly Func<ReadOnlyMemory<byte>, DetailEntries> DecodeDetails = ReadDetails;

  static readonly Func<ReadOnlyMemory<byte>, DistinctEntries> DecodeDistinct = ReadDistinct;

  [ThreadStatic]
  static ByteWriter threadWriter;

  [ThreadStatic]
  static ByteWriter threadSectionWriter;

  [ThreadStatic]
  static ByteWriter threadEntryWriter;

  /// <summary>
  /// Reads the entries of the detail section: their lengths only, each detail is decoded when read.
  /// </summary>
  static DetailEntries ReadDetails(ReadOnlyMemory<byte> bytes)
  {
    try
    {
      var reader = new ByteReader(bytes.Span);
      // A value without details takes one byte, its zero length.
      var entries = new RollupSection<ValueDetail>[CheckedCount(ref reader, 1)];
      for (var i = 0; i < entries.Length; ++i)
      {
        var entry = ReadLengthPrefixed(ref reader, bytes, allowEmpty: true);
        if (!entry.IsEmpty)
          entries[i] = new(entry, DecodeDetail);
      }
      RequireEnd(reader);
      return new DetailEntries(entries);
    }
    catch (Exception exception) when (IsMalformed(exception))
    {
      throw new FormatException("The rollup value details are malformed.", exception);
    }
  }

  /// <summary>
  /// Reads the names and the entries of the distinct section; each sketch is decoded when read.
  /// </summary>
  static DistinctEntries ReadDistinct(ReadOnlyMemory<byte> bytes)
  {
    try
    {
      var reader = new ByteReader(bytes.Span);
      // Every entry takes at least a name length, its length and a sparse sketch's precision, format and count.
      var count = CheckedCount(ref reader, 5);
      var names = new string[count];
      var sketches = new RollupSection<HyperLogLog>[count];
      for (var i = 0; i < count; ++i)
      {
        names[i] = reader.ReadString() ?? throw new FormatException("A distinct dimension has no name.");
        sketches[i] = new(ReadLengthPrefixed(ref reader, bytes, allowEmpty: false), DecodeVisitors);
      }
      RequireEnd(reader);
      return new DistinctEntries(names, sketches);
    }
    catch (Exception exception) when (IsMalformed(exception))
    {
      throw new FormatException("The rollup value distinct sketches are malformed.", exception);
    }
  }

  /// <summary>
  /// Reads the length of a section or entry and returns its bytes without decoding them.
  /// Only a detail entry may be empty: sections are written only when they hold something.
  /// </summary>
  static ReadOnlyMemory<byte> ReadLengthPrefixed(ref ByteReader reader, ReadOnlyMemory<byte> bytes, bool allowEmpty)
  {
    var length = reader.ReadVarUInt64();
    if ((length == 0 && !allowEmpty) || length > (ulong)reader.Remaining)
      throw new FormatException("A rollup value section or entry is empty or truncated.");
    var start = bytes.Length - reader.Remaining;
    reader.ReadBytes((int)length);
    return bytes.Slice(start, (int)length);
  }

  static void RequireEnd(ByteReader reader)
  {
    if (reader.Remaining != 0)
      throw new FormatException("A rollup value section has trailing bytes.");
  }

  /// <summary>The exceptions that malformed bytes raise while they are read.</summary>
  static bool IsMalformed(Exception exception) =>
      exception is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException;

  /// <summary>Decodes a whole section; malformed bytes raise <see cref="FormatException"/>.</summary>
  static T Decode<T>(ReadOnlyMemory<byte> bytes, SectionReader<T> read)
  {
    try
    {
      var reader = new ByteReader(bytes.Span);
      var result = read(ref reader);
      if (reader.Remaining != 0)
        throw new FormatException("A rollup value section has trailing bytes.");
      return result;
    }
    catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
    {
      throw new FormatException("A rollup value section is malformed.", exception);
    }
  }

  public RollupValue Deserialize(Memory<byte> bytes)
  {
    try
    {
      return Read(bytes);
    }
    catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
    {
      throw new FormatException("The rollup value is malformed or truncated.", exception);
    }
  }

  static RollupValue Read(ReadOnlyMemory<byte> bytes)
  {
    var reader = new ByteReader(bytes.Span);
    var header = reader.ReadByte();
    if (header == Deleted)
    {
      if (reader.Remaining != 0)
        throw new FormatException("A deleted rollup value has trailing bytes.");
      return null;
    }
    if ((header & ~KnownBits) != 0 || (header & Present) == 0)
      throw new FormatException("The rollup value has an unknown layout.");
    var count = reader.ReadVarInt64();
    // Every value takes at least a name length, its sum and its count.
    var nameCount = CheckedCount(ref reader, 10);
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
    if ((header & (HasVisitors | HasDetails | HasDistinct)) == 0)
    {
      if (reader.Remaining != 0)
        throw new FormatException("The rollup value has trailing bytes.");
      return new RollupValue(count, null, names, values, null, null);
    }
    // The sections follow the scalar values. They keep their stored bytes for as long as the row
    // lives, and ZoneTree reads values from decompressed disk blocks of megabytes: a slice of a
    // block would keep the whole block alive in caches and in merged rows, so the sections of a
    // value that does not own its buffer are copied into one buffer of their own.
    var start = bytes.Length - reader.Remaining;
    var sections = OwnsBuffer(bytes) ? bytes[start..] : bytes[start..].ToArray();
    var sectionReader = new ByteReader(sections.Span);
    RollupSection<HyperLogLog> visitors = null;
    RollupSection<DetailEntries> details = null;
    RollupSection<DistinctEntries> distinct = null;
    if ((header & HasVisitors) != 0)
      visitors = new(ReadLengthPrefixed(ref sectionReader, sections, allowEmpty: false), DecodeVisitors);
    if ((header & HasDetails) != 0)
    {
      var section = ReadLengthPrefixed(ref sectionReader, sections, allowEmpty: false);
      // Details align with the values: their count is checked now, the details decoded when read.
      var detailReader = new ByteReader(section.Span);
      if (detailReader.ReadVarUInt64() != (ulong)nameCount)
        throw new FormatException("The rollup value details do not match its values.");
      details = new(section, DecodeDetails);
    }
    if ((header & HasDistinct) != 0)
      distinct = new(ReadLengthPrefixed(ref sectionReader, sections, allowEmpty: false), DecodeDistinct);
    if (sectionReader.Remaining != 0)
      throw new FormatException("The rollup value has trailing bytes.");
    return new RollupValue(count, visitors, names, values, details, distinct);
  }

  /// <summary>
  /// Reads a count of entries that take at least <paramref name="minimumEntryBytes"/> each. A
  /// count the remaining bytes cannot hold is corrupt, not an allocation to try.
  /// </summary>
  static int CheckedCount(ref ByteReader reader, int minimumEntryBytes)
  {
    var count = reader.ReadVarUInt64();
    if (count > (ulong)(reader.Remaining / minimumEntryBytes))
      throw new FormatException("A rollup value count exceeds its bytes.");
    return (int)count;
  }

  /// <summary>Whether the bytes are a whole array, which holds nothing but this value.</summary>
  static bool OwnsBuffer(ReadOnlyMemory<byte> bytes) =>
      MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Offset == 0 && segment.Count == segment.Array.Length;

  public Memory<byte> Serialize(in RollupValue entry)
  {
    if (entry == null)
      return new byte[] { Deleted };
    var writer = ByteWriter.Reuse(ref threadWriter, 256);
    var visitors = entry.VisitorSection;
    var details = entry.DetailSection;
    var distinct = entry.DistinctSection;
    var header = Present;
    if (visitors != null)
      header |= HasVisitors;
    if (details != null)
      header |= HasDetails;
    if (distinct != null)
      header |= HasDistinct;
    writer.WriteByte(header);
    writer.WriteVarInt64(entry.Count);
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
    if (visitors != null)
      WriteSection(writer, visitors, ref threadSectionWriter, static (section, sketch) => sketch.Write(section));
    if (details != null)
    {
      WriteSection(writer, details, ref threadSectionWriter, static (section, entries) =>
      {
        section.WriteVarUInt64((ulong)entries.Entries.Length);
        foreach (var detail in entries.Entries)
        {
          if (detail == null)
            section.WriteVarUInt64(0);
          else
            WriteSection(section, detail, ref threadEntryWriter, static (entry, value) => value.Write(entry));
        }
      });
    }
    if (distinct != null)
    {
      WriteSection(writer, distinct, ref threadSectionWriter, static (section, entries) =>
      {
        section.WriteVarUInt64((ulong)entries.Names.Length);
        for (var i = 0; i < entries.Names.Length; ++i)
        {
          section.WriteString(entries.Names[i]);
          WriteSection(section, entries.Sketches[i], ref threadEntryWriter, static (entry, sketch) => sketch.Write(entry));
        }
      });
    }
    return writer.ToArray();
  }

  /// <summary>
  /// Writes a length-prefixed section or entry: its stored bytes as they are, or its value
  /// encoded into <paramref name="scratch"/>.
  /// </summary>
  static void WriteSection<T>(ByteWriter writer, RollupSection<T> section, ref ByteWriter scratch, Action<ByteWriter, T> encode)
      where T : class
  {
    if (section.IsStored)
    {
      writer.WriteVarUInt64((ulong)section.Bytes.Length);
      writer.WriteBytes(section.Bytes.Span);
      return;
    }
    var encoded = ByteWriter.Reuse(ref scratch, 256);
    encode(encoded, section.Value);
    writer.WriteVarUInt64((ulong)encoded.Written.Length);
    writer.WriteBytes(encoded.Written);
  }
}

