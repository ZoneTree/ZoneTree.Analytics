using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Serializers;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The aggregate of one measured value.
/// </summary>
/// <param name="Sum">The sum of values.</param>
/// <param name="Count">The number of values.</param>
/// <param name="Min">The smallest value.</param>
/// <param name="Max">The largest value.</param>
readonly record struct MetricValue(double Sum, long Count, double Min, double Max)
{
  /// <summary>
  /// The average of the values, or zero when there are none.
  /// </summary>
  public double Average => Count == 0 ? 0 : Sum / Count;
}

/// <summary>
/// A stored rollup row. Instances are immutable: merges create new instances,
/// so values held by ZoneTree segments are never modified.
/// </summary>
sealed class RollupValue
{
  public readonly long Count;

  public readonly HyperLogLog Visitors;

  public readonly string[] MetricNames;

  public readonly MetricValue[] Metrics;

  public RollupValue(long count, HyperLogLog visitors, string[] metricNames, MetricValue[] metrics)
  {
    Count = count;
    Visitors = visitors;
    MetricNames = metricNames ?? [];
    Metrics = metrics ?? [];
  }

  public static RollupValue Merge(RollupValue existing, RollupValue delta)
  {
    if (existing == null)
      return delta;
    if (delta == null)
      return existing;
    HyperLogLog visitors;
    if (delta.Visitors == null || delta.Visitors.IsEmpty)
    {
      visitors = existing.Visitors;
    }
    else if (existing.Visitors == null || existing.Visitors.IsEmpty)
    {
      visitors = delta.Visitors;
    }
    else
    {
      visitors = existing.Visitors.Clone();
      visitors.UnionWith(delta.Visitors);
    }

    string[] names;
    MetricValue[] metrics;
    if (delta.MetricNames.Length == 0)
    {
      names = existing.MetricNames;
      metrics = existing.Metrics;
    }
    else if (existing.MetricNames.Length == 0)
    {
      names = delta.MetricNames;
      metrics = delta.Metrics;
    }
    else
    {
      MergeMetrics(existing, delta, out names, out metrics);
    }
    return new RollupValue(existing.Count + delta.Count, visitors, names, metrics);
  }

  static void MergeMetrics(
      RollupValue a, RollupValue b, out string[] names, out MetricValue[] metrics)
  {
    var resultNames = new List<string>(a.MetricNames.Length + b.MetricNames.Length);
    var resultValues = new List<MetricValue>(resultNames.Capacity);
    int i = 0, j = 0;
    while (i < a.MetricNames.Length || j < b.MetricNames.Length)
    {
      var cmp = i == a.MetricNames.Length ? 1 :
          j == b.MetricNames.Length ? -1 :
          string.CompareOrdinal(a.MetricNames[i], b.MetricNames[j]);
      if (cmp < 0)
      {
        resultNames.Add(a.MetricNames[i]);
        resultValues.Add(a.Metrics[i++]);
      }
      else if (cmp > 0)
      {
        resultNames.Add(b.MetricNames[j]);
        resultValues.Add(b.Metrics[j++]);
      }
      else
      {
        resultNames.Add(a.MetricNames[i]);
        resultValues.Add(Combine(a.Metrics[i++], b.Metrics[j++]));
      }
    }
    names = [.. resultNames];
    metrics = [.. resultValues];
  }

  public static MetricValue Combine(MetricValue a, MetricValue b)
  {
    if (a.Count == 0)
      return new MetricValue(a.Sum + b.Sum, b.Count, b.Min, b.Max);
    if (b.Count == 0)
      return new MetricValue(a.Sum + b.Sum, a.Count, a.Min, a.Max);
    return new MetricValue(
        a.Sum + b.Sum, a.Count + b.Count, Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max));
  }
}

/// <summary>
/// A mutable aggregate used by ingestion buffers and query groups.
/// </summary>
sealed class RollupAccumulator
{
  public long Count;

  HyperLogLog visitors;

  bool ownsVisitors;

  Dictionary<string, MetricValue> metrics;

  public HyperLogLog Visitors => visitors;

  public IReadOnlyDictionary<string, MetricValue> Metrics =>
      (IReadOnlyDictionary<string, MetricValue>)metrics ?? EmptyMetrics;

  static readonly Dictionary<string, MetricValue> EmptyMetrics = [];

  /// <summary>
  /// True when the accumulator holds a count, a visitor or a metric value. Groups whose deltas
  /// cancel out, for example an exit that moved on to a later page, hold no data.
  /// </summary>
  public bool HasData
  {
    get
    {
      if (Count != 0 || (visitors != null && !visitors.IsEmpty))
        return true;
      if (metrics != null)
      {
        foreach (var value in metrics.Values)
        {
          if (value.Sum != 0 || value.Count != 0)
            return true;
        }
      }
      return false;
    }
  }

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
  public void AddMetric(string name, double value)
  {
    metrics ??= new(StringComparer.Ordinal);
    if (metrics.TryGetValue(name, out var current))
      metrics[name] = RollupValue.Combine(current, new MetricValue(value, 1, value, value));
    else
      metrics[name] = new MetricValue(value, 1, value, value);
  }

  /// <summary>
  /// Adds a delta to a sum-only metric.
  /// </summary>
  public void AddMetricDelta(string name, double delta)
  {
    metrics ??= new(StringComparer.Ordinal);
    if (metrics.TryGetValue(name, out var current))
      metrics[name] = current with { Sum = current.Sum + delta };
    else
      metrics[name] = new MetricValue(delta, 0, 0, 0);
  }

  public void Merge(RollupValue value)
  {
    if (value == null)
      return;
    Count += value.Count;
    if (value.Visitors != null && !value.Visitors.IsEmpty)
    {
      if (visitors == null)
      {
        // Share the stored sketch until this accumulator needs to modify it.
        visitors = value.Visitors;
        ownsVisitors = false;
      }
      else
      {
        EnsureOwnVisitors(visitors.Precision);
        visitors.UnionWith(value.Visitors);
      }
    }
    for (var i = 0; i < value.MetricNames.Length; ++i)
      MergeMetric(value.MetricNames[i], value.Metrics[i]);
  }

  public void Merge(RollupAccumulator other)
  {
    Count += other.Count;
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
    if (other.metrics != null)
    {
      foreach (var (name, value) in other.metrics)
        MergeMetric(name, value);
    }
  }

  void MergeMetric(string name, MetricValue value)
  {
    metrics ??= new(StringComparer.Ordinal);
    if (metrics.TryGetValue(name, out var current))
      metrics[name] = RollupValue.Combine(current, value);
    else
      metrics[name] = value;
  }

  /// <summary>
  /// Creates an immutable value. The accumulator must not be used afterwards.
  /// </summary>
  public RollupValue ToValue()
  {
    string[] names = [];
    MetricValue[] values = [];
    if (metrics != null && metrics.Count > 0)
    {
      names = [.. metrics.Keys];
      Array.Sort(names, StringComparer.Ordinal);
      values = new MetricValue[names.Length];
      for (var i = 0; i < names.Length; ++i)
        values[i] = metrics[names[i]];
    }
    var sketch = visitors == null || visitors.IsEmpty ? null : visitors;
    // Stored sketches are shared with readers on other threads: they must be compact.
    sketch?.Compact();
    ownsVisitors = false;
    return new RollupValue(Count, sketch, names, values);
  }
}

/// <summary>
/// Binary serializer of <see cref="RollupValue"/>. The type name is part of the
/// persisted ZoneTree metadata and must stay stable.
/// </summary>
/// <remarks>
/// <code>
/// header(1: 0 = deleted, otherwise Present | HasVisitors) count(varint) [sketch]
/// metricCount(varint) [name(string) sum(double) count(varint) [min(double) max(double)]]*
/// </code>
/// </remarks>
sealed class RollupValueSerializer : ISerializer<RollupValue>
{
  const byte Deleted = 0;

  const byte Present = 1;

  const byte HasVisitors = 2;

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
    var metricCount = (int)reader.ReadVarUInt64();
    var names = new string[metricCount];
    var metrics = new MetricValue[metricCount];
    for (var i = 0; i < metricCount; ++i)
    {
      names[i] = reader.ReadString();
      var sum = reader.ReadDouble();
      var valueCount = reader.ReadVarInt64();
      double min = 0, max = 0;
      if (valueCount != 0)
      {
        min = reader.ReadDouble();
        max = reader.ReadDouble();
      }
      metrics[i] = new MetricValue(sum, valueCount, min, max);
    }
    return new RollupValue(count, visitors, names, metrics);
  }

  public Memory<byte> Serialize(in RollupValue entry)
  {
    if (entry == null)
      return new byte[] { Deleted };
    var writer = threadWriter ??= new ByteWriter(256);
    writer.Reset();
    var hasVisitors = entry.Visitors != null && !entry.Visitors.IsEmpty;
    writer.WriteByte(hasVisitors ? (byte)(Present | HasVisitors) : Present);
    writer.WriteVarInt64(entry.Count);
    if (hasVisitors)
      entry.Visitors.Write(writer);
    writer.WriteVarUInt64((ulong)entry.MetricNames.Length);
    for (var i = 0; i < entry.MetricNames.Length; ++i)
    {
      var metric = entry.Metrics[i];
      writer.WriteString(entry.MetricNames[i]);
      writer.WriteDouble(metric.Sum);
      writer.WriteVarInt64(metric.Count);
      if (metric.Count != 0)
      {
        writer.WriteDouble(metric.Min);
        writer.WriteDouble(metric.Max);
      }
    }
    return writer.ToArray();
  }
}
