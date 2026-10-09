using System.Buffers.Binary;
using System.Text;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// A decoded raw event.
/// </summary>
sealed class RawEvent
{
  public long Ticks;

  public string Name;

  public ulong Visitor;

  public ulong SessionId;

  public long SessionStartTicks;

  public bool Interactive;

  public readonly List<KeyValuePair<string, string>> Dimensions = [];

  public readonly List<KeyValuePair<string, double>> Metrics = [];

  public bool TryGetDimension(string name, out string value)
  {
    foreach (var (key, v) in Dimensions)
    {
      if (key == name)
      {
        value = v;
        return true;
      }
    }
    value = null;
    return false;
  }

  public void Clear()
  {
    Name = null;
    Visitor = 0;
    SessionId = 0;
    SessionStartTicks = 0;
    Interactive = false;
    Dimensions.Clear();
    Metrics.Clear();
  }
}

/// <summary>
/// The set of dimensions a raw scan decodes. Other dimensions are skipped without
/// allocating strings.
/// </summary>
sealed class DimensionSelector
{
  readonly byte[][] utf8Names;

  public DimensionSelector(IEnumerable<string> names)
  {
    Names = names.Distinct(StringComparer.Ordinal).ToArray();
    utf8Names = Names.Select(Encoding.UTF8.GetBytes).ToArray();
  }

  public string[] Names { get; }

  public int IndexOf(ReadOnlySpan<byte> utf8Name)
  {
    for (var i = 0; i < utf8Names.Length; ++i)
    {
      if (utf8Name.SequenceEqual(utf8Names[i]))
        return i;
    }
    return -1;
  }
}

/// <summary>
/// Binary encoding of raw event values.
/// </summary>
/// <remarks>
/// <code>
/// name(string) visitor(8) session(8) sessionStartOffset(varint) flags(1)
/// dimensionCount(varint) [name(string) value(string)]*
/// metricCount(varint) [name(string) value(double)]*
/// </code>
/// The timestamp and site are part of the key.
/// </remarks>
static class RawEventCodec
{
  const byte InteractiveFlag = 1;

  [ThreadStatic]
  static ByteWriter threadWriter;

  public static Memory<byte> Encode(
      string name,
      long ticks,
      ulong visitor,
      ulong sessionId,
      long sessionStartTicks,
      bool interactive,
      IReadOnlyDictionary<string, string> dimensions,
      IReadOnlyList<KeyValuePair<string, double>> metrics)
  {
    var writer = threadWriter ??= new ByteWriter(512);
    writer.Reset();
    writer.WriteString(name);
    Span<byte> fixed8 = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(fixed8, visitor);
    writer.WriteBytes(fixed8);
    BinaryPrimitives.WriteUInt64LittleEndian(fixed8, sessionId);
    writer.WriteBytes(fixed8);
    writer.WriteVarInt64(sessionId == 0 ? 0 : ticks - sessionStartTicks);
    writer.WriteByte(interactive ? InteractiveFlag : (byte)0);
    writer.WriteVarUInt64((ulong)(dimensions?.Count ?? 0));
    if (dimensions != null)
    {
      foreach (var (key, value) in dimensions)
      {
        writer.WriteString(key);
        writer.WriteString(value);
      }
    }
    writer.WriteVarUInt64((ulong)(metrics?.Count ?? 0));
    if (metrics != null)
    {
      foreach (var (key, value) in metrics)
      {
        writer.WriteString(key);
        writer.WriteDouble(value);
      }
    }
    return writer.ToArray();
  }

  /// <summary>
  /// Decodes an event. When <paramref name="expectedName"/> is not null and the stored
  /// name differs, decoding stops early and false is returned.
  /// </summary>
  public static bool TryDecode(
      ReadOnlySpan<byte> bytes,
      long ticks,
      string expectedName,
      ReadOnlySpan<byte> expectedNameUtf8,
      RawEvent target,
      DimensionSelector selector = null)
  {
    target.Clear();
    var reader = new ByteReader(bytes);
    if (expectedName != null)
    {
      if (!reader.ReadStringEquals(expectedNameUtf8))
        return false;
      target.Name = expectedName;
    }
    else
    {
      target.Name = reader.ReadString();
    }
    target.Ticks = ticks;
    target.Visitor = BinaryPrimitives.ReadUInt64LittleEndian(reader.ReadBytes(8));
    target.SessionId = BinaryPrimitives.ReadUInt64LittleEndian(reader.ReadBytes(8));
    target.SessionStartTicks = ticks - reader.ReadVarInt64();
    target.Interactive = (reader.ReadByte() & InteractiveFlag) != 0;
    var dimensionCount = (int)reader.ReadVarUInt64();
    for (var i = 0; i < dimensionCount; ++i)
    {
      if (selector == null)
      {
        var key = reader.ReadString();
        var value = reader.ReadString();
        target.Dimensions.Add(new(key, value));
        continue;
      }
      // Only decode the dimensions the query needs; compare names as UTF-8 bytes.
      var index = selector.IndexOf(reader.ReadStringBytes());
      if (index < 0)
      {
        reader.SkipString();
        continue;
      }
      target.Dimensions.Add(new(selector.Names[index], reader.ReadString()));
    }
    var metricCount = (int)reader.ReadVarUInt64();
    for (var i = 0; i < metricCount; ++i)
    {
      var key = reader.ReadString();
      var value = reader.ReadDouble();
      target.Metrics.Add(new(key, value));
    }
    return true;
  }

  /// <summary>
  /// Reads only the visitor hash of an encoded event.
  /// </summary>
  public static ulong ReadVisitor(ReadOnlySpan<byte> bytes)
  {
    var reader = new ByteReader(bytes);
    reader.SkipString();
    return BinaryPrimitives.ReadUInt64LittleEndian(reader.ReadBytes(8));
  }

  public static byte[] Utf8(string value) =>
      string.IsNullOrEmpty(value) ? [] : Encoding.UTF8.GetBytes(value);
}
