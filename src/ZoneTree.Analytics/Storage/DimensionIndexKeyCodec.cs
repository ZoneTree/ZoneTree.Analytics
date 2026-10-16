using System.Buffers.Binary;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// A shared tree holds every indexed dimension. Keys are dimension, site, length-prefixed value,
/// UTC timestamp and the original raw event sequence. Payloads use RawEventCodec unchanged.
/// Length prefixes preserve empty values and prevent embedded zero bytes from aliasing keys.
/// </summary>
static class DimensionIndexKeyCodec
{
  [ThreadStatic]
  static ByteWriter writer;

  public static byte[] Namespace(string dimension)
  {
    var output = ByteWriter.Reuse(ref writer, 256);
    output.WriteRawUtf8(dimension);
    output.WriteByte(0);
    return output.ToArray();
  }

  public static byte[] Prefix(ReadOnlySpan<byte> indexNamespace, string site, string value)
  {
    var output = ByteWriter.Reuse(ref writer, 256);
    WritePrefix(output, indexNamespace, site, value);
    return output.ToArray();
  }

  static void WritePrefix(ByteWriter output, ReadOnlySpan<byte> indexNamespace, string site, string value)
  {
    output.WriteBytes(indexNamespace);
    output.WriteRawUtf8(site);
    output.WriteByte(0);
    output.WriteString(value);
  }

  public static Memory<byte> Key(ReadOnlySpan<byte> indexNamespace, string site, string value, long ticks, long sequence)
  {
    var output = ByteWriter.Reuse(ref writer, 256);
    WritePrefix(output, indexNamespace, site, value);
    output.WriteUInt64BigEndian((ulong)ticks);
    output.WriteUInt64BigEndian((ulong)sequence);
    return output.ToArray();
  }

  public static long Sequence(ReadOnlySpan<byte> key) =>
      (long)BinaryPrimitives.ReadUInt64BigEndian(key[^8..]);

  /// <summary>The head of an index key: everything before its timestamp and sequence.</summary>
  public static int HeadLength(ReadOnlySpan<byte> key) => key.Length - 16;
}
