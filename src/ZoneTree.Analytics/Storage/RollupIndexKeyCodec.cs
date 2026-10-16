using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Storage;

/// <summary>index dimension, joint definition, site, event, length-prefixed equality value, bucket, complete original rollup key.</summary>
static class RollupIndexKeyCodec
{
  [ThreadStatic] static ByteWriter writer;

  public static byte[] Namespace(string dimension, string definitionId)
  {
    var output = ByteWriter.Reuse(ref writer, 256);
    output.WriteRawUtf8(dimension);
    output.WriteByte(0);
    output.WriteRawUtf8(definitionId);
    output.WriteByte(0);
    return output.ToArray();
  }

  public static byte[] Prefix(ReadOnlySpan<byte> indexNamespace, string site, string eventName, string value)
  {
    var output = ByteWriter.Reuse(ref writer, 256);
    WritePrefix(output, indexNamespace, site, eventName, value);
    return output.ToArray();
  }

  static void WritePrefix(ByteWriter output, ReadOnlySpan<byte> indexNamespace, string site, string eventName, string value)
  {
    output.WriteBytes(indexNamespace);
    output.WriteRawUtf8(site);
    output.WriteByte(0);
    output.WriteRawUtf8(eventName);
    output.WriteByte(0);
    output.WriteString(value);
  }

  public static Memory<byte> Key(ReadOnlySpan<byte> indexNamespace, string site, string eventName, string value, long bucket, ReadOnlySpan<byte> originalKey)
  {
    var output = ByteWriter.Reuse(ref writer, indexNamespace.Length + 256 + originalKey.Length);
    WritePrefix(output, indexNamespace, site, eventName, value);
    output.WriteUInt64BigEndian((ulong)bucket);
    output.WriteBytes(originalKey);
    return output.ToArray();
  }

  public static int HeadLength(ReadOnlySpan<byte> key)
  {
    var segments = KeyCodec.HeadLength(key, 4);
    var reader = new ByteReader(key[segments..]);
    reader.ReadStringBytes();
    return key.Length - reader.Remaining;
  }

  public static ReadOnlySpan<byte> OriginalKey(ReadOnlySpan<byte> key) => key[(HeadLength(key) + 8)..];
}
