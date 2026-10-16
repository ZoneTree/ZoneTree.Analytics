using System.Buffers.Binary;
using System.Text;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The length of the head of a key: its leading bytes, after which the keys of one head are
/// ordered by time.
/// </summary>
delegate int KeyHeadLength(ReadOnlySpan<byte> key);

/// <summary>
/// Order-preserving binary key layouts.
/// </summary>
/// <remarks>
/// Keys are compared lexicographically, a key before every longer key it is a prefix of
/// (ZoneTree's <c>ByteArrayLexicographicComparerAscending</c>). Variable-length segments are
/// terminated by a zero byte, which never occurs inside sanitized names, so a segment never
/// prefixes another.
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

  /// <summary>
  /// Stored in place of a dimension the event does not carry. Control characters never occur
  /// in sanitized values, so it cannot collide with real data.
  /// </summary>
  public const string MissingValue = "\u0001";

  /// <summary>
  /// Converts a stored value part to its public form: null for a missing dimension.
  /// </summary>
  public static string Public(string part) => part == MissingValue ? null : part;

  /// <summary>
  /// Converts a public value to its stored form.
  /// </summary>
  public static string Stored(string value) => value ?? MissingValue;

  [ThreadStatic]
  static ByteWriter threadWriter;

  static ByteWriter Writer => ByteWriter.Reuse(ref threadWriter, 256);

  static void WriteSegment(ByteWriter writer, string segment)
  {
    writer.WriteRawUtf8(segment);
    writer.WriteByte(Terminator);
  }

  /// <summary>
  /// Writes the head of a rollup series: site, event and dimension set.
  /// </summary>
  static void WriteSeries(ByteWriter writer, string site, string eventName, string dimensionSetId)
  {
    WriteSegment(writer, site);
    WriteSegment(writer, eventName);
    WriteSegment(writer, dimensionSetId);
  }

  /// <summary>
  /// Builds the prefix shared by every row of one rollup series.
  /// </summary>
  public static byte[] RollupPrefix(string site, string eventName, string dimensionSetId)
  {
    var writer = Writer;
    WriteSeries(writer, site, eventName, dimensionSetId);
    return writer.ToArray();
  }

  /// <summary>
  /// Builds the prefix of every rollup row of a site.
  /// </summary>
  public static byte[] SitePrefix(string site)
  {
    var writer = Writer;
    WriteSegment(writer, site);
    return writer.ToArray();
  }

  public static Memory<byte> RollupKey(
      string site, string eventName, string dimensionSetId, long bucket, string value)
  {
    var writer = Writer;
    WriteSeries(writer, site, eventName, dimensionSetId);
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
  /// Splits a combined value into one value per dimension. Sanitized values contain no control
  /// characters, so the separator never occurs inside a value.
  /// </summary>
  public static string[] SplitValues(string combined, int dimensionCount) => dimensionCount switch
  {
    0 => [],
    1 => [combined],
    _ => combined.Split(ValueSeparator)
  };

  public static Memory<byte> RawKey(string site, long ticks, long sequence)
  {
    var writer = Writer;
    WriteSegment(writer, site);
    writer.WriteUInt64BigEndian((ulong)ticks);
    writer.WriteUInt64BigEndian((ulong)sequence);
    return writer.ToArray();
  }

  public static long ReadRawTicks(ReadOnlySpan<byte> key, int sitePrefixLength)
  {
    return (long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(sitePrefixLength, 8));
  }

  /// <summary>
  /// The timestamp of a raw key, read from its fixed-size end without locating the site.
  /// </summary>
  public static long ReadRawTicks(ReadOnlySpan<byte> key) =>
      (long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(key.Length - 16, 8));

  /// <summary>
  /// The bucket of a rollup key: the 8 bytes after its third terminator.
  /// </summary>
  public static long ReadRollupBucket(ReadOnlySpan<byte> key) =>
      (long)BinaryPrimitives.ReadUInt64BigEndian(key.Slice(HeadLength(key, 3), 8));

  /// <summary>
  /// The dimension-set identifier of a rollup key: its third segment, read without decoding
  /// the rest of the key.
  /// </summary>
  public static string ReadDimensionSetId(ReadOnlySpan<byte> key) =>
      ReadSegment(key, HeadLength(key, 2), out _);

  /// <summary>
  /// The length of the leading segments of a key up to and including its
  /// <paramref name="segments"/>-th terminator: the site of a raw key (1), or the site, event and
  /// dimension set of a rollup key (3). Rows of one head are ordered by time.
  /// </summary>
  public static int HeadLength(ReadOnlySpan<byte> key, int segments)
  {
    var length = 0;
    for (var i = 0; i < segments; ++i)
      length += key[length..].IndexOf(Terminator) + 1;
    return length;
  }

  /// <summary>The head of a raw key: its site.</summary>
  public static int RawEventHead(ReadOnlySpan<byte> key) => HeadLength(key, 1);

  /// <summary>The head of a rollup key: its site, event and dimension set.</summary>
  public static int RollupHead(ReadOnlySpan<byte> key) => HeadLength(key, 3);

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

  /// <summary>
  /// Returns the smallest key greater than every key that starts with the given prefix.
  /// </summary>
  public static Memory<byte> AfterPrefix(ReadOnlySpan<byte> prefix)
  {
    for (var i = prefix.Length - 1; i >= 0; --i)
    {
      if (prefix[i] == byte.MaxValue) continue;
      var next = prefix[..(i + 1)].ToArray();
      ++next[i];
      return next;
    }
    throw new InvalidOperationException("A key prefix must have a successor.");
  }
}
