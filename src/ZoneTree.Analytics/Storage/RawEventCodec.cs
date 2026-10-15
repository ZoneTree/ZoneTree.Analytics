using System.Buffers.Binary;
using System.Text;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// A decoded raw event.
/// </summary>
public sealed class RawEvent
{
  /// <summary>The stored site, populated when decoding a complete key/value pair.</summary>
  public string Site;

  /// <summary>The event sequence from the key.</summary>
  public long Sequence;

  /// <summary>The event's UTC timestamp ticks.</summary>
  public long Ticks;

  /// <summary>The event's timestamp as a UTC instant.</summary>
  public DateTimeOffset Timestamp => new(Ticks, TimeSpan.Zero);

  /// <summary>The event's complete storage key.</summary>
  public RawEventKey Key => new(Site, Ticks, Sequence);

  /// <summary>The stored event name.</summary>
  public string Name;

  /// <summary>The stored visitor hash, not the original visitor identifier.</summary>
  public ulong Visitor;

  /// <summary>The session identifier, or zero when the event has no session.</summary>
  public ulong SessionId;

  /// <summary>The session start's UTC ticks; meaningful only when SessionId is nonzero.</summary>
  public long SessionStartTicks;

  /// <summary>The session start as a UTC instant, or null when the event has no session.</summary>
  public DateTimeOffset? SessionStart => SessionId == 0 ? null : new DateTimeOffset(SessionStartTicks, TimeSpan.Zero);

  /// <summary>Whether the event counts as a session interaction.</summary>
  public bool Interactive;

  /// <summary>The stored dimension names and values, in encoded order.</summary>
  public readonly List<KeyValuePair<string, string>> Dimensions = [];

  /// <summary>The stored measured value names and numbers, in encoded order.</summary>
  public readonly List<KeyValuePair<string, double>> Values = [];

  /// <summary>
  /// Decoded strings, reused across the rows of a scan.
  /// </summary>
  Utf8StringCache strings;

  internal Utf8StringCache Strings => strings ??= new();

  /// <summary>Looks up a dimension by name.</summary>
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

  /// <summary>Looks up a measured value by name.</summary>
  public bool TryGetValue(string name, out double value)
  {
    foreach (var (key, number) in Values)
    {
      if (key == name)
      {
        value = number;
        return true;
      }
    }
    value = 0;
    return false;
  }

  /// <summary>
  /// True when the event has the name and matches every filter.
  /// </summary>
  internal bool Matches(string eventName, Filter[] filters, ReportingCalendar calendar)
  {
    if (Name != eventName)
      return false;
    var dimensions = new FilterValues(this, calendar);
    foreach (var filter in filters)
    {
      if (filter != null && !filter.IsMatch(dimensions))
        return false;
    }
    return true;
  }

  internal void Clear()
  {
    Site = null;
    Sequence = 0;
    Name = null;
    Visitor = 0;
    SessionId = 0;
    SessionStartTicks = 0;
    Interactive = false;
    Dimensions.Clear();
    Values.Clear();
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
    Names = names.Where(x => !TimeDimension.IsDerived(x)).Distinct(StringComparer.Ordinal).ToArray();
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
/// valueCount(varint) [name(string) value(double)]*
/// </code>
/// The timestamp and site are part of the key.
/// </remarks>
public static class RawEventCodec
{
  const byte InteractiveFlag = 1;

  [ThreadStatic]
  static ByteWriter threadWriter;

  /// <summary>Encodes the key of a decoded raw event.</summary>
  public static Memory<byte> EncodeKey(RawEvent rawEvent)
  {
    ArgumentNullException.ThrowIfNull(rawEvent);
    return StorageKeyCodec.EncodeRawEvent(rawEvent.Key);
  }

  /// <summary>Encodes a raw event value in the engine's storage format. Null encodes a deletion.</summary>
  /// <remarks>The site, timestamp and sequence belong to the key returned by <see cref="EncodeKey"/>.</remarks>
  /// <exception cref="ArgumentException">A stored name or string is invalid.</exception>
  /// <exception cref="ArgumentOutOfRangeException">An event or session timestamp is outside the supported date range.</exception>
  public static Memory<byte> Encode(RawEvent rawEvent)
  {
    if (rawEvent == null)
      return Memory<byte>.Empty;
    Text.ValidateStoredName(rawEvent.Name, nameof(rawEvent));
    ArgumentOutOfRangeException.ThrowIfNegative(rawEvent.Ticks, nameof(rawEvent));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(rawEvent.Ticks, DateTime.MaxValue.Ticks, nameof(rawEvent));
    if (rawEvent.SessionId != 0)
    {
      ArgumentOutOfRangeException.ThrowIfNegative(rawEvent.SessionStartTicks, nameof(rawEvent));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(rawEvent.SessionStartTicks, DateTime.MaxValue.Ticks, nameof(rawEvent));
    }
    foreach (var (name, value) in rawEvent.Dimensions)
    {
      Text.ValidateStoredName(name, nameof(rawEvent));
      // Ingestion never stores control characters; rollup keys reserve some of them.
      if (value != null && value.Any(char.IsControl))
        throw new ArgumentException("Dimension values cannot contain control characters.", nameof(rawEvent));
      Text.ValidateUnicode(value, nameof(rawEvent));
    }
    foreach (var (name, _) in rawEvent.Values)
      Text.ValidateStoredName(name, nameof(rawEvent));
    return Encode(rawEvent.Name, rawEvent.Ticks, rawEvent.Visitor, rawEvent.SessionId,
        rawEvent.SessionStartTicks, rawEvent.Interactive,
        rawEvent.Dimensions, rawEvent.Values);
  }

  /// <summary>
  /// Decodes an iterator's raw key and value into a complete event, including its site,
  /// timestamp, sequence, visitor hash, session, dimensions and measured values.
  /// Returns an independent data record without a scan cache, or null for an empty value (a deletion marker).
  /// </summary>
  /// <exception cref="FormatException">The key or value is malformed.</exception>
  public static RawEvent Decode(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
  {
    var decodedKey = StorageKeyCodec.DecodeRawEvent(key);
    if (value.IsEmpty)
      return null;
    var result = new RawEvent();
    try
    {
      TryDecode(value, decodedKey.Ticks, null, default, result, requireEnd: true);
    }
    catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or DecoderFallbackException)
    {
      throw new FormatException("The raw event value is malformed or truncated.", exception);
    }
    result.Site = decodedKey.Site;
    result.Sequence = decodedKey.Sequence;
    return result;
  }

  internal static Memory<byte> Encode(
      string name,
      long ticks,
      ulong visitor,
      ulong sessionId,
      long sessionStartTicks,
      bool interactive,
      IReadOnlyCollection<KeyValuePair<string, string>> dimensions,
      IReadOnlyList<KeyValuePair<string, double>> values)
  {
    var writer = ByteWriter.Reuse(ref threadWriter, 512);
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
    writer.WriteVarUInt64((ulong)(values?.Count ?? 0));
    if (values != null)
    {
      foreach (var (key, value) in values)
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
  internal static bool TryDecode(
      ReadOnlySpan<byte> bytes,
      long ticks,
      string expectedName,
      ReadOnlySpan<byte> expectedNameUtf8,
      RawEvent target,
      DimensionSelector selector = null,
      bool requireEnd = false)
  {
    target.Clear();
    var reader = new ByteReader(bytes, strict: requireEnd);
    // Public results keep only decoded data. Query scans reuse a cache with their target.
    var strings = requireEnd ? null : target.Strings;
    if (expectedName != null)
    {
      if (!reader.ReadStringEquals(expectedNameUtf8))
        return false;
      target.Name = expectedName;
    }
    else
    {
      target.Name = reader.ReadString(strings);
    }
    target.Ticks = ticks;
    target.Visitor = BinaryPrimitives.ReadUInt64LittleEndian(reader.ReadBytes(8));
    target.SessionId = BinaryPrimitives.ReadUInt64LittleEndian(reader.ReadBytes(8));
    var sessionOffset = reader.ReadVarInt64();
    target.SessionStartTicks = checked(ticks - sessionOffset);
    var flags = reader.ReadByte();
    target.Interactive = (flags & InteractiveFlag) != 0;
    if (requireEnd && (string.IsNullOrWhiteSpace(target.Name) || target.Name.Any(char.IsControl) || (flags & ~InteractiveFlag) != 0 ||
        (target.SessionId == 0 && sessionOffset != 0) ||
        target.SessionStartTicks < 0 || target.SessionStartTicks > DateTime.MaxValue.Ticks))
      throw new FormatException("The raw event header contains an invalid name, session start or flags.");
    var dimensionCount = checked((int)reader.ReadVarUInt64());
    for (var i = 0; i < dimensionCount; ++i)
    {
      if (selector == null)
      {
        var key = reader.ReadString(strings);
        if (requireEnd && (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)))
          throw new FormatException("A raw event dimension has an invalid name.");
        var value = reader.ReadString(strings);
        if (requireEnd && value != null && value.Any(char.IsControl))
          throw new FormatException("A raw event dimension value contains a control character.");
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
      target.Dimensions.Add(new(selector.Names[index], reader.ReadString(strings)));
    }
    var valueCount = checked((int)reader.ReadVarUInt64());
    for (var i = 0; i < valueCount; ++i)
    {
      var key = reader.ReadString(strings);
      if (requireEnd && (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)))
        throw new FormatException("A raw event measured value has an invalid name.");
      var value = reader.ReadDouble();
      target.Values.Add(new(key, value));
    }
    if (requireEnd && reader.Remaining != 0)
      throw new FormatException("The raw event value contains trailing bytes.");
    return true;
  }

  /// <summary>
  /// Reads only the visitor hash of an encoded event.
  /// </summary>
  internal static ulong ReadVisitor(ReadOnlySpan<byte> bytes)
  {
    var reader = new ByteReader(bytes);
    reader.SkipString();
    return BinaryPrimitives.ReadUInt64LittleEndian(reader.ReadBytes(8));
  }

  /// <summary>Reads only the session id, without decoding the event name or body.</summary>
  internal static ulong ReadSessionId(ReadOnlySpan<byte> bytes)
  {
    var reader = new ByteReader(bytes);
    reader.SkipString();
    return BinaryPrimitives.ReadUInt64LittleEndian(reader.ReadBytes(16)[8..]);
  }

  internal static byte[] Utf8(string value) =>
      string.IsNullOrEmpty(value) ? [] : Encoding.UTF8.GetBytes(value);
}
