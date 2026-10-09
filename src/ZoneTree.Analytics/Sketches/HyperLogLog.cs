using System.Numerics;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Sketches;

/// <summary>
/// A mergeable distinct-count sketch.
/// </summary>
/// <remarks>
/// <para>
/// Small sets are kept in an exact sparse representation: a sorted array holding the top
/// 32 bits of each item's 64-bit hash. Sets with up to <c>2^precision / 4</c> items are
/// counted exactly (barring 32-bit hash collisions, which are negligible at that size),
/// and a sparse sketch never uses more memory than a dense one.
/// </para>
/// <para>
/// Larger sets switch to a dense HyperLogLog register array and are estimated with
/// Otmar Ertl's improved estimator, which is unbiased across the whole cardinality range
/// without empirical bias-correction tables. The standard error of the dense estimate is
/// about <c>1.04 / sqrt(2^precision)</c>: 0.81% for the default precision of 14.
/// </para>
/// <para>
/// Instances are mutable and not thread-safe. Sketches stored in ZoneTree are treated as
/// immutable snapshots: merges stored back to ZoneTree always work on a <see cref="Clone"/>.
/// </para>
/// </remarks>
sealed class HyperLogLog
{
  /// <summary>
  /// The default precision. Dense sketches use 2^14 one-byte registers (16 KiB).
  /// </summary>
  public const int DefaultPrecision = 14;

  /// <summary>
  /// The minimum supported precision.
  /// </summary>
  public const int MinPrecision = 8;

  /// <summary>
  /// The maximum supported precision.
  /// </summary>
  public const int MaxPrecision = 18;

  const byte SparseFormat = 1;

  const byte DenseFormat = 2;

  const int PendingCapacity = 64;

  readonly int precision;

  uint[] sparse;

  int sparseCount;

  byte[] registers;

  // Unsorted recent additions, merged into the sorted sparse array in batches so that
  // building a sparse sketch costs O(n log n) instead of O(n^2).
  uint[] pending;

  int pendingCount;

  /// <summary>
  /// Creates an empty sketch.
  /// </summary>
  /// <param name="precision">The number of index bits.</param>
  public HyperLogLog(int precision = DefaultPrecision)
  {
    ValidatePrecision(precision);
    this.precision = precision;
    sparse = [];
  }

  HyperLogLog(int precision, uint[] sparse, int sparseCount, byte[] registers)
  {
    this.precision = precision;
    this.sparse = sparse;
    this.sparseCount = sparseCount;
    this.registers = registers;
  }

  /// <summary>
  /// The sketch precision.
  /// </summary>
  public int Precision => precision;

  /// <summary>
  /// True while the sketch counts exactly.
  /// </summary>
  public bool IsSparse
  {
    get
    {
      Compact();
      return registers == null;
    }
  }

  /// <summary>
  /// True if no element was added.
  /// </summary>
  public bool IsEmpty => registers == null && sparseCount == 0 && pendingCount == 0;

  /// <summary>
  /// The largest number of items counted exactly.
  /// </summary>
  public int ExactLimit => 1 << (precision - 2);

  /// <summary>
  /// Validates the precision argument.
  /// </summary>
  public static void ValidatePrecision(int precision)
  {
    if (precision < MinPrecision || precision > MaxPrecision)
      throw new ArgumentOutOfRangeException(
          nameof(precision),
          $"HyperLogLog precision must be between {MinPrecision} and {MaxPrecision}.");
  }

  /// <summary>
  /// Adds a 64-bit item hash. The hash is mixed internally,
  /// so callers may pass identifiers that are not uniformly distributed.
  /// </summary>
  public void Add(ulong itemHash)
  {
    var hash = Mix(itemHash);
    if (registers != null)
    {
      AddToRegisters(registers, precision, hash);
      return;
    }
    var entry = (uint)(hash >> 32);
    if (pendingCount == 0 && sparseCount < 8)
    {
      InsertSorted(entry);
      return;
    }
    pending ??= new uint[PendingCapacity];
    pending[pendingCount++] = entry;
    if (pendingCount == PendingCapacity)
      Compact();
  }

  void InsertSorted(uint entry)
  {
    var index = Array.BinarySearch(sparse, 0, sparseCount, entry);
    if (index >= 0)
      return;
    index = ~index;
    if (sparseCount == sparse.Length)
      Array.Resize(ref sparse, Math.Max(4, sparse.Length * 2));
    if (index < sparseCount)
      Array.Copy(sparse, index, sparse, index + 1, sparseCount - index);
    sparse[index] = entry;
    ++sparseCount;
  }

  /// <summary>
  /// Merges pending additions into the sorted sparse array.
  /// </summary>
  internal void Compact()
  {
    if (pendingCount == 0)
      return;
    var count = pendingCount;
    pendingCount = 0;
    Array.Sort(pending, 0, count);
    MergeSparse(pending, count);
  }

  void ConvertToDense()
  {
    Compact();
    var dense = new byte[1 << precision];
    for (var i = 0; i < sparseCount; ++i)
      AddToRegisters(dense, precision, FromSparse(sparse[i]));
    registers = dense;
    sparse = null;
    sparseCount = 0;
    pending = null;
    pendingCount = 0;
  }

  /// <summary>
  /// Expands a sparse entry to a 64-bit hash. The unknown low bits are set to ones, so the
  /// derived rank never exceeds what the 32 known bits support.
  /// </summary>
  static ulong FromSparse(uint entry) => ((ulong)entry << 32) | 0xFFFFFFFFUL;

  static void AddToRegisters(byte[] registers, int precision, ulong hash)
  {
    var index = (int)(hash >> (64 - precision));
    var q = 64 - precision;
    var rank = Math.Min(BitOperations.LeadingZeroCount(hash << precision) + 1, q + 1);
    if (registers[index] < rank)
      registers[index] = (byte)rank;
  }

  /// <summary>
  /// Merges another sketch into this sketch.
  /// </summary>
  public void UnionWith(HyperLogLog other)
  {
    if (other == null || other.IsEmpty)
      return;
    if (other.precision != precision)
      throw new InvalidOperationException(
          "HyperLogLog sketches with different precisions cannot be merged.");
    // Sketches held by ZoneTree are always compact; only worker-local sketches have pending items.
    other.Compact();
    Compact();
    if (other.registers != null)
    {
      if (registers == null)
        ConvertToDense();
      var target = registers;
      var source = other.registers;
      for (var i = 0; i < target.Length; ++i)
      {
        if (target[i] < source[i])
          target[i] = source[i];
      }
      return;
    }
    if (registers != null)
    {
      for (var i = 0; i < other.sparseCount; ++i)
        AddToRegisters(registers, precision, FromSparse(other.sparse[i]));
      return;
    }
    MergeSparse(other.sparse, other.sparseCount);
  }

  void MergeSparse(uint[] otherEntries, int otherCount)
  {
    // Both inputs are sorted. They are merged in place, from the largest entry down, into the
    // sparse array grown by doubling, so repeated merges do not allocate. Duplicates, within or
    // across inputs, are written once. The write position never passes the unread sparse
    // entries: it starts otherCount entries above them and drops by at most one per entry read.
    var end = sparseCount + otherCount;
    if (sparse.Length < end)
      Array.Resize(ref sparse, Math.Max(end, sparse.Length * 2));
    int i = sparseCount - 1, j = otherCount - 1, k = end;
    while (i >= 0 || j >= 0)
    {
      uint next;
      if (j < 0 || (i >= 0 && sparse[i] > otherEntries[j]))
        next = sparse[i--];
      else
        next = otherEntries[j--];
      if (k == end || sparse[k] != next)
        sparse[--k] = next;
    }
    sparseCount = end - k;
    if (k > 0)
      Array.Copy(sparse, k, sparse, 0, sparseCount);
    if (sparseCount > ExactLimit)
      ConvertToDense();
  }

  /// <summary>
  /// Returns the estimated number of distinct items.
  /// The result is exact while the sketch is sparse.
  /// </summary>
  public long Estimate()
  {
    Compact();
    if (registers == null)
      return sparseCount;
    return (long)Math.Round(EstimateDense(registers, precision));
  }

  static double EstimateDense(byte[] registers, int precision)
  {
    // Otmar Ertl, "New cardinality estimation algorithms for HyperLogLog sketches", 2017.
    var m = registers.Length;
    var q = 64 - precision;
    Span<int> histogram = stackalloc int[q + 2];
    foreach (var register in registers)
      ++histogram[register];
    double z = m * Tau(1.0 - (double)histogram[q + 1] / m);
    for (var k = q; k >= 1; --k)
    {
      z += histogram[k];
      z *= 0.5;
    }
    z += m * Sigma((double)histogram[0] / m);
    const double alphaInfinity = 0.72134752044448170367996234050095; // 1 / (2 ln 2)
    return alphaInfinity * m * m / z;
  }

  static double Sigma(double x)
  {
    if (x == 1.0)
      return double.PositiveInfinity;
    double y = 1.0;
    double z = x;
    double previous;
    do
    {
      x *= x;
      previous = z;
      z += x * y;
      y += y;
    } while (z != previous);
    return z;
  }

  static double Tau(double x)
  {
    if (x == 0.0 || x == 1.0)
      return 0.0;
    double y = 1.0;
    double z = 1.0 - x;
    double previous;
    do
    {
      x = Math.Sqrt(x);
      previous = z;
      y *= 0.5;
      var t = 1.0 - x;
      z -= t * t * y;
    } while (z != previous);
    return z / 3.0;
  }

  /// <summary>
  /// Creates a deep copy of the sketch.
  /// </summary>
  public HyperLogLog Clone()
  {
    Compact();
    if (registers != null)
      return new HyperLogLog(precision, null, 0, (byte[])registers.Clone());
    return new HyperLogLog(precision, sparse.AsSpan(0, sparseCount).ToArray(), sparseCount, null);
  }

  /// <summary>
  /// Writes the sketch in its compact binary form: sparse entries as deltas, dense registers as is.
  /// </summary>
  internal void Write(ByteWriter writer)
  {
    Compact();
    writer.WriteByte((byte)precision);
    if (registers != null)
    {
      writer.WriteByte(DenseFormat);
      writer.WriteBytes(registers);
      return;
    }
    writer.WriteByte(SparseFormat);
    writer.WriteVarUInt64((ulong)sparseCount);
    uint previous = 0;
    for (var i = 0; i < sparseCount; ++i)
    {
      var value = sparse[i];
      writer.WriteVarUInt64(value - previous);
      previous = value;
    }
  }

  /// <summary>
  /// Reads a sketch written by <see cref="Write"/>.
  /// </summary>
  internal static HyperLogLog Read(ref ByteReader reader)
  {
    var precision = reader.ReadByte();
    ValidatePrecision(precision);
    var format = reader.ReadByte();
    if (format == DenseFormat)
    {
      var registers = reader.ReadBytes(1 << precision).ToArray();
      return new HyperLogLog(precision, null, 0, registers);
    }
    if (format != SparseFormat)
      throw new FormatException("Unknown HyperLogLog format.");
    var count = (int)reader.ReadVarUInt64();
    var entries = new uint[count];
    uint previous = 0;
    for (var i = 0; i < count; ++i)
    {
      previous += (uint)reader.ReadVarUInt64();
      entries[i] = previous;
    }
    return new HyperLogLog(precision, entries, count, null);
  }

  /// <summary>
  /// SplitMix64 finalizer. Spreads arbitrary identifiers over the 64-bit space.
  /// </summary>
  static ulong Mix(ulong x)
  {
    x ^= x >> 30;
    x *= 0xBF58476D1CE4E5B9UL;
    x ^= x >> 27;
    x *= 0x94D049BB133111EBUL;
    x ^= x >> 31;
    return x;
  }
}
