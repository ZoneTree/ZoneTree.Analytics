using System.Numerics;

namespace ZoneTree.Analytics.Geo;

/// <summary>
/// Sorted address ranges and their locations. A search reads a prefix index over 65,536 equal
/// slices of the occupied address span; in a crowded slice, every 16th range start narrows it to
/// 16 ranges, so a search touches a few cache lines however unevenly ranges are spread.
/// </summary>
/// <typeparam name="T">The address: uint for IPv4, UInt128 for IPv6.</typeparam>
sealed class AddressRanges<T> where T : struct, IBinaryInteger<T>, IUnsignedNumber<T>
{
  const int Slices = 1 << 16;

  const int Block = 16;

  readonly T[] starts;
  readonly int[] locations;
  // samples[j] is starts[j * Block].
  readonly T[] samples;
  // index[s] is the first range starting at or after indexBase + (s << shift).
  readonly int[] index;
  readonly T indexBase;
  readonly int shift;

  /// <param name="starts">Sorted range starts, the first at address zero.</param>
  /// <param name="locations">The location of each range.</param>
  public AddressRanges(T[] starts, int[] locations)
  {
    this.starts = starts;
    this.locations = locations;
    samples = new T[(starts.Length + Block - 1) / Block];
    for (var j = 0; j < samples.Length; j++)
      samples[j] = starts[j * Block];
    // The slices span the ranges after the first, which starts at zero and is usually unknown.
    var first = starts.Length > 1 ? starts[1] : T.Zero;
    var last = starts[^1];
    while ((last - (first >> shift << shift)) >> shift >= T.CreateTruncating(Slices))
      shift++;
    indexBase = first >> shift << shift;
    index = new int[Slices + 1];
    var range = 0;
    for (var slice = 0; slice <= Slices; slice++)
    {
      var offset = T.CreateTruncating(slice) << shift;
      var bound = indexBase + offset;
      var beyondAddresses = offset >> shift != T.CreateTruncating(slice) || bound < indexBase;
      while (range < starts.Length && (beyondAddresses || starts[range] < bound))
        range++;
      index[slice] = range;
    }
  }

  public int Count => starts.Length;

  /// <summary>
  /// The location of the range of an address: the last range starting at or before it.
  /// </summary>
  public int Find(T address)
  {
    int low, high;
    if (address < indexBase)
    {
      low = 0;
      high = index[0];
    }
    else
    {
      var slice = (address - indexBase) >> shift;
      if (slice >= T.CreateTruncating(Slices))
      {
        low = index[Slices];
        high = starts.Length;
      }
      else
      {
        var s = int.CreateTruncating(slice);
        low = index[s];
        high = index[s + 1];
      }
    }
    // The range is in [low - 1, high - 1]: low - 1 when no range of the slice starts at or before
    // the address. Ranges start at zero, so low - 1 is a range whenever that happens.
    if (high - low > Block)
    {
      int first = (low + Block - 1) / Block, end = (high - 1) / Block + 1;
      var after = UpperBound(samples, first, end, address);
      if (after == first)
      {
        high = first * Block;
      }
      else
      {
        low = (after - 1) * Block + 1;
        high = Math.Min(high, after * Block);
      }
    }
    return locations[UpperBound(starts, low, high, address) - 1];
  }

  // The first position in [low, high) whose key is greater than the address, or high.
  static int UpperBound(T[] keys, int low, int high, T address)
  {
    while (low < high)
    {
      var middle = (int)((uint)(low + high) >> 1);
      if (keys[middle] <= address) low = middle + 1;
      else high = middle;
    }
    return low;
  }
}
