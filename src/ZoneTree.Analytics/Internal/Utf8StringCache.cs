using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ZoneTree.Analytics.Internal;

/// <summary>
/// A small direct-mapped cache of decoded ASCII strings.
/// </summary>
/// <remarks>
/// Raw scans decode the same event names, metric names and dimension values on most rows.
/// A hit returns the cached string instead of allocating a new one; a miss decodes and
/// replaces the slot, so memory stays bounded however many distinct values a scan reads.
/// Not thread-safe: each scan owns its cache.
/// </remarks>
sealed class Utf8StringCache
{
  const int SlotBits = 12;

  const int SlotCount = 1 << SlotBits;

  const int MaxCachedLength = 128;

  readonly string[] slots = new string[SlotCount];

  public string Get(ReadOnlySpan<byte> utf8)
  {
    if (utf8.IsEmpty)
      return string.Empty;
    if (utf8.Length > MaxCachedLength)
      return Encoding.UTF8.GetString(utf8);
    ref var slot = ref slots[SlotOf(utf8)];
    if (slot != null && Ascii.Equals(utf8, slot))
      return slot;
    var value = Encoding.UTF8.GetString(utf8);
    if (Ascii.IsValid(utf8))
      slot = value;
    return value;
  }

  // Hashes the length and up to 16 edge bytes: a few instructions per value, and values that
  // differ only in the middle merely share a slot.
  static int SlotOf(ReadOnlySpan<byte> utf8)
  {
    var length = utf8.Length;
    ulong head, tail;
    if (length >= 8)
    {
      head = BinaryPrimitives.ReadUInt64LittleEndian(utf8);
      tail = BinaryPrimitives.ReadUInt64LittleEndian(utf8[^8..]);
    }
    else if (length >= 4)
    {
      head = BinaryPrimitives.ReadUInt32LittleEndian(utf8);
      tail = BinaryPrimitives.ReadUInt32LittleEndian(utf8[^4..]);
    }
    else
    {
      head = utf8[0] | ((ulong)utf8[length - 1] << 8) | ((ulong)utf8[length / 2] << 16);
      tail = 0;
    }
    var hash = (head ^ BitOperations.RotateLeft(tail, 29) ^ (ulong)length) * 0x9E3779B97F4A7C15UL;
    return (int)(hash >> (64 - SlotBits));
  }
}
