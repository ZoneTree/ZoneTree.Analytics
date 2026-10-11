using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Sketches;

/// <summary>
/// The bucket of a value in a <see cref="QuantileSketch"/>. Computing it once lets one value be
/// added to many sketches, for example every rollup row an event contributes to, without
/// repeating the logarithm.
/// </summary>
readonly struct QuantileBucket
{
  internal QuantileBucket(int store, int index)
  {
    Store = store;
    Index = index;
  }

  /// <summary>1 for positive values, -1 for negative values, 0 for values near zero.</summary>
  internal int Store { get; }

  internal int Index { get; }
}

/// <summary>
/// A mergeable quantile sketch with a relative-error guarantee (DDSketch).
/// </summary>
/// <remarks>
/// <para>
/// A positive value v is counted in bucket ceil(log_γ(v)), with γ = (1 + α) / (1 − α); negative
/// values are counted the same way in a mirrored store, and values within 1e-9 of zero in a zero
/// bucket. A quantile is answered with the representative value of the bucket that holds its
/// rank, which is within a relative error α = 1% of the true value of that rank. Merging adds
/// bucket counts: it is exact and independent of order, so a merged sketch answers exactly as a
/// sketch of all values would.
/// </para>
/// <para>
/// Each store keeps at most 2,048 buckets, which spans 17 orders of magnitude; beyond that the
/// smallest magnitudes share a bucket. Instances are mutable and not thread-safe. Sketches
/// stored in ZoneTree are immutable snapshots: merges stored back always work on a copy.
/// </para>
/// <para>
/// Masson, Rim and Lee, "DDSketch: A fast and fully-mergeable quantile sketch with
/// relative-error guarantees", VLDB 2019.
/// </para>
/// </remarks>
public sealed class QuantileSketch
{
  /// <summary>
  /// The relative accuracy α of quantiles.
  /// </summary>
  public const double RelativeAccuracy = 0.01;

  const double MinMagnitude = 1e-9;

  const int MaxBuckets = 2048;

  static readonly double Gamma = (1 + RelativeAccuracy) / (1 - RelativeAccuracy);

  static readonly double LogGamma = Math.Log(Gamma);

  static readonly double LogRepresentativeScale = Math.Log(1 - RelativeAccuracy);

  readonly Store positive;

  readonly Store negative;

  long zeroCount;

  /// <summary>Creates an empty sketch.</summary>
  public QuantileSketch()
  {
    positive = new Store();
    negative = new Store();
  }

  QuantileSketch(Store positive, Store negative, long zeroCount)
  {
    this.positive = positive;
    this.negative = negative;
    this.zeroCount = zeroCount;
  }

  /// <summary>The number of values.</summary>
  public long Count => positive.Total + negative.Total + zeroCount;

  /// <summary>Whether no values have been added.</summary>
  public bool IsEmpty => Count == 0;

  internal static QuantileBucket BucketOf(double value)
  {
    if (value >= MinMagnitude)
      return new QuantileBucket(1, IndexOf(value));
    if (value <= -MinMagnitude)
      return new QuantileBucket(-1, IndexOf(-value));
    return new QuantileBucket(0, 0);
  }

  static int IndexOf(double magnitude) => (int)Math.Ceiling(Math.Log(magnitude) / LogGamma);

  /// <summary>
  /// The value that represents bucket <paramref name="index"/>: every value of the bucket is
  /// within the relative accuracy of it.
  /// </summary>
  static double ValueOf(int index) =>
      Math.Min(double.MaxValue, Math.Exp(index * LogGamma + LogRepresentativeScale));

  /// <summary>Adds one measured value.</summary>
  public void Add(double value) => Add(BucketOf(value));

  internal void Add(QuantileBucket bucket)
  {
    switch (bucket.Store)
    {
      case 1:
        positive.Add(bucket.Index, 1);
        break;
      case -1:
        negative.Add(bucket.Index, 1);
        break;
      default:
        ++zeroCount;
        break;
    }
  }

  /// <summary>Merges another sketch into this instance.</summary>
  public void MergeWith(QuantileSketch other)
  {
    positive.MergeWith(other.positive);
    negative.MergeWith(other.negative);
    zeroCount += other.zeroCount;
  }

  /// <summary>Creates a deep copy of the sketch.</summary>
  public QuantileSketch Clone() => new(positive.Clone(), negative.Clone(), zeroCount);

  /// <summary>
  /// The value at quantile <paramref name="q"/>, 0 to 1, or zero for an empty sketch.
  /// </summary>
  public double Quantile(double q)
  {
    var count = Count;
    if (count == 0)
      return 0;
    // The rank of the value, counted from zero, among the values in ascending order.
    var rank = q * (count - 1);
    long seen = 0;
    // Negative values in ascending order: largest magnitudes first.
    for (var index = negative.MaxIndex; negative.Total > 0 && index >= negative.MinIndex; --index)
    {
      seen += negative.CountAt(index);
      if (seen > rank)
        return -ValueOf(index);
    }
    seen += zeroCount;
    if (seen > rank)
      return 0;
    for (var index = positive.MinIndex; positive.Total > 0 && index <= positive.MaxIndex; ++index)
    {
      seen += positive.CountAt(index);
      if (seen > rank)
        return ValueOf(index);
    }
    return positive.Total > 0 ? ValueOf(positive.MaxIndex) : 0;
  }

  /// <summary>
  /// The number of values whose bucket's representative value is at least
  /// <paramref name="lower"/> and below <paramref name="upper"/>: values within the relative
  /// accuracy of a bound may count on either side of it.
  /// </summary>
  public long CountBetween(double lower, double upper)
  {
    long count = 0;
    for (var index = negative.MinIndex; negative.Total > 0 && index <= negative.MaxIndex; ++index)
    {
      var value = -ValueOf(index);
      if (value >= lower && value < upper)
        count += negative.CountAt(index);
    }
    if (0 >= lower && 0 < upper)
      count += zeroCount;
    for (var index = positive.MinIndex; positive.Total > 0 && index <= positive.MaxIndex; ++index)
    {
      var value = ValueOf(index);
      if (value >= lower && value < upper)
        count += positive.CountAt(index);
    }
    return count;
  }

  internal void Write(ByteWriter writer)
  {
    writer.WriteVarUInt64((ulong)zeroCount);
    positive.Write(writer);
    negative.Write(writer);
  }

  internal static QuantileSketch Read(ref ByteReader reader)
  {
    var zeroCount = (long)reader.ReadVarUInt64();
    var positive = Store.Read(ref reader);
    var negative = Store.Read(ref reader);
    return new QuantileSketch(positive, negative, zeroCount);
  }

  /// <summary>
  /// Bucket counts over a contiguous index range.
  /// </summary>
  sealed class Store
  {
    // counts[i] is the count of bucket offset + i; buckets MinIndex..MaxIndex are in use.
    long[] counts;

    int offset;

    public int MinIndex { get; private set; }

    public int MaxIndex { get; private set; }

    public long Total { get; private set; }

    public long CountAt(int index) => counts[index - offset];

    public void Add(int index, long count)
    {
      if (count == 0)
        return;
      if (Total == 0)
      {
        counts ??= new long[8];
        Array.Clear(counts);
        offset = index - counts.Length / 2;
        MinIndex = MaxIndex = index;
      }
      else if (index < MinIndex || index > MaxIndex)
      {
        index = Extend(index);
      }
      counts[index - offset] += count;
      Total += count;
    }

    /// <summary>
    /// Makes room for a bucket outside the used range and returns the index to count it in.
    /// Beyond <see cref="MaxBuckets"/> buckets, the smallest magnitudes fold into the lowest
    /// bucket that is kept.
    /// </summary>
    int Extend(int index)
    {
      var newMin = Math.Min(MinIndex, index);
      var newMax = Math.Max(MaxIndex, index);
      if (newMax - newMin + 1 > MaxBuckets)
      {
        newMin = newMax - MaxBuckets + 1;
        index = Math.Max(index, newMin);
      }
      if (newMin <= MinIndex && newMin >= offset && newMax < offset + counts.Length)
      {
        MinIndex = newMin;
        MaxIndex = newMax;
        return index;
      }
      var span = newMax - newMin + 1;
      var length = Math.Max(span, Math.Min(counts.Length * 2, MaxBuckets));
      var newOffset = newMin - (length - span) / 2;
      var rebuilt = new long[length];
      for (var i = MinIndex; i <= MaxIndex; ++i)
        rebuilt[Math.Max(i, newMin) - newOffset] += counts[i - offset];
      counts = rebuilt;
      offset = newOffset;
      MinIndex = newMin;
      MaxIndex = newMax;
      return index;
    }

    public void MergeWith(Store other)
    {
      if (other.Total == 0)
        return;
      for (var index = other.MinIndex; index <= other.MaxIndex; ++index)
        Add(index, other.counts[index - other.offset]);
    }

    public Store Clone()
    {
      var clone = new Store();
      if (Total == 0)
        return clone;
      clone.counts = new long[MaxIndex - MinIndex + 1];
      Array.Copy(counts, MinIndex - offset, clone.counts, 0, clone.counts.Length);
      clone.offset = MinIndex;
      clone.MinIndex = MinIndex;
      clone.MaxIndex = MaxIndex;
      clone.Total = Total;
      return clone;
    }

    public void Write(ByteWriter writer)
    {
      if (Total == 0)
      {
        writer.WriteVarUInt64(0);
        return;
      }
      writer.WriteVarUInt64((ulong)(MaxIndex - MinIndex + 1));
      writer.WriteVarInt64(MinIndex);
      for (var index = MinIndex; index <= MaxIndex; ++index)
        writer.WriteVarUInt64((ulong)counts[index - offset]);
    }

    public static Store Read(ref ByteReader reader)
    {
      var store = new Store();
      var length = (int)reader.ReadVarUInt64();
      if (length == 0)
        return store;
      if (length > MaxBuckets)
        throw new FormatException("Invalid quantile sketch.");
      var min = (int)reader.ReadVarInt64();
      store.counts = new long[length];
      store.offset = min;
      store.MinIndex = min;
      store.MaxIndex = min + length - 1;
      long total = 0;
      for (var i = 0; i < length; ++i)
      {
        var count = (long)reader.ReadVarUInt64();
        store.counts[i] = count;
        total += count;
      }
      store.Total = total;
      return store;
    }
  }
}
