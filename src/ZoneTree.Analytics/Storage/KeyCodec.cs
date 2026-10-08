using System.Buffers.Binary;
using System.Text;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Order-preserving binary key layouts.
/// </summary>
/// <remarks>
/// Keys are compared with ZoneTree's built-in byte-array comparer, which orders
/// keys lexicographically. Variable-length segments are terminated by a zero byte,
/// which never occurs inside sanitized names, so a segment never prefixes another.
/// Timestamps are big-endian so byte order equals numeric order.
/// <code>
/// rollup key: site 0x00 event 0x00 dimension-set 0x00 bucket(8, BE) values(UTF-8, 0x1F separated)
/// raw key:    site 0x00 ticks(8, BE) sequence(8, BE)
/// </code>
/// One rollup query (site, event, dimension set, time range) is a single contiguous
/// range scan, regardless of how many buckets or values it covers.
/// </remarks>
static class KeyCodec
{
  public const byte Terminator = 0;

  public const char ValueSeparator = '\u001F';

  [ThreadStatic]
  static ByteWriter threadWriter;

  static ByteWriter Writer
  {
    get
    {
      var writer = threadWriter ??= new ByteWriter(256);
      writer.Reset();
      return writer;
    }
  }

  /// <summary>
  /// Builds the prefix shared by every row of one rollup series.
  /// </summary>
  public static byte[] RollupPrefix(string site, string eventName, string dimensionSetId)
  {
    var writer = Writer;
    writer.WriteRawUtf8(site);
    writer.WriteByte(Terminator);
    writer.WriteRawUtf8(eventName);
    writer.WriteByte(Terminator);
    writer.WriteRawUtf8(dimensionSetId);
    writer.WriteByte(Terminator);
    return writer.ToArray();
  }

  /// <summary>
  /// Builds the prefix of every rollup row of a site.
  /// </summary>
  public static byte[] SitePrefix(string site)
  {
    var writer = Writer;
    writer.WriteRawUtf8(site);
    writer.WriteByte(Terminator);
    return writer.ToArray();
  }

  public static Memory<byte> RollupKey(ReadOnlySpan<byte> prefix, long bucket, string value)
  {
    var writer = Writer;
    writer.WriteBytes(prefix);
    writer.WriteUInt64BigEndian((ulong)bucket);
    writer.WriteRawUtf8(value);
    return writer.ToArray();
  }

  public static Memory<byte> RollupKey(
      string site, string eventName, string dimensionSetId, long bucket, string value)
  {
    var writer = Writer;
    writer.WriteRawUtf8(site);
    writer.WriteByte(Terminator);
    writer.WriteRawUtf8(eventName);
    writer.WriteByte(Terminator);
    writer.WriteRawUtf8(dimensionSetId);
    writer.WriteByte(Terminator);
    writer.WriteUInt64BigEndian((ulong)bucket);
    writer.WriteRawUtf8(value);
    return writer.ToArray();
  }

  public static Memory<byte> SeekKey(ReadOnlySpan<byte> prefix, long bucket)
  {
    var writer = Writer;
    writer.WriteBytes(prefix);
    writer.WriteUInt64BigEndian((ulong)bucket);
    return writer.ToArray();
  }

  public static long ReadBucket(ReadOnlySpan<byte> key, int prefixLength)
  {
    return (long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(prefixLength, 8));
  }

  public static string ReadValue(ReadOnlySpan<byte> key, int prefixLength)
  {
    var start = prefixLength + 8;
    return start >= key.Length ? "" : Encoding.UTF8.GetString(key[start..]);
  }

  /// <summary>
  /// Splits a combined value into one value per dimension.
  /// </summary>
  public static string[] SplitValues(string combined, int dimensionCount)
  {
    if (dimensionCount == 0)
      return [];
    if (dimensionCount == 1)
      return [combined];
    var parts = combined.Split(ValueSeparator);
    if (parts.Length == dimensionCount)
      return parts;
    Array.Resize(ref parts, dimensionCount);
    for (var i = 0; i < parts.Length; ++i)
      parts[i] ??= "";
    return parts;
  }

  public static Memory<byte> RawKey(string site, long ticks, long sequence)
  {
    var writer = Writer;
    writer.WriteRawUtf8(site);
    writer.WriteByte(Terminator);
    writer.WriteUInt64BigEndian((ulong)ticks);
    writer.WriteUInt64BigEndian((ulong)sequence);
    return writer.ToArray();
  }

  public static long ReadRawTicks(ReadOnlySpan<byte> key, int sitePrefixLength)
  {
    return (long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(sitePrefixLength, 8));
  }

  /// <summary>
  /// Reads the segment that starts at <paramref name="offset"/> and ends at the next terminator.
  /// </summary>
  public static string ReadSegment(ReadOnlySpan<byte> key, int offset, out int next)
  {
    var end = key[offset..].IndexOf(Terminator);
    if (end < 0)
    {
      next = key.Length;
      return Encoding.UTF8.GetString(key[offset..]);
    }
    next = offset + end + 1;
    return Encoding.UTF8.GetString(key.Slice(offset, end));
  }

  /// <summary>
  /// Returns the smallest key greater than every key that starts with the given prefix
  /// followed by a terminator: prefix + 0x01.
  /// </summary>
  public static Memory<byte> AfterSegment(ReadOnlySpan<byte> prefixWithSegment)
  {
    var result = new byte[prefixWithSegment.Length + 1];
    prefixWithSegment.CopyTo(result);
    result[^1] = 1;
    return result;
  }
}
