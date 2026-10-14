using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Storage;

/// <summary>site, event, length-prefixed equality value, bucket, complete original rollup key.</summary>
static class RollupIndexKeyCodec
{
  [ThreadStatic] static ByteWriter writer;

  public static byte[] Prefix(string site, string eventName, string value)
  {
    var output = ByteWriter.Reuse(ref writer, 256);
    output.WriteRawUtf8(site);
    output.WriteByte(0);
    output.WriteRawUtf8(eventName);
    output.WriteByte(0);
    output.WriteString(value);
    return output.ToArray();
  }

  public static Memory<byte> Key(string site, string eventName, string value, long bucket, ReadOnlySpan<byte> originalKey)
  {
    var prefix = Prefix(site, eventName, value);
    var output = ByteWriter.Reuse(ref writer, prefix.Length + 8 + originalKey.Length);
    output.WriteBytes(prefix);
    output.WriteUInt64BigEndian((ulong)bucket);
    output.WriteBytes(originalKey);
    return output.ToArray();
  }

  public static int HeadLength(ReadOnlySpan<byte> key)
  {
    var segments = KeyCodec.HeadLength(key, 2);
    var reader = new ByteReader(key[segments..]);
    reader.ReadStringBytes();
    return key.Length - reader.Remaining;
  }

  public static ReadOnlySpan<byte> OriginalKey(ReadOnlySpan<byte> key) => key[(HeadLength(key) + 8)..];

  public static bool IsRangeExpired(ReadOnlySpan<byte> first, ReadOnlySpan<byte> last, ExpiredKeyPredicate expired)
  {
    var head = HeadLength(first);
    return last.Length > head && last[..head].SequenceEqual(first[..head]) && expired(last);
  }

  public static int DeleteFirstExpired(IZoneTree<Memory<byte>, RollupValue> tree, ExpiredKeyPredicate expired)
  {
    var count = 0;
    using var iterator = tree.CreateIterator(new IteratorOptions { IteratorType = IteratorType.NoRefresh, IncludeDeletedRecords = true });
    iterator.SeekFirst();
    while (iterator.Next())
    {
      var key = iterator.CurrentKey;
      if (expired(key.Span)) { tree.ForceDelete(key); ++count; }
      iterator.Seek(DimensionIndexKeyCodec.AfterPrefix(key.Span[..HeadLength(key.Span)]));
    }
    return count;
  }
}
