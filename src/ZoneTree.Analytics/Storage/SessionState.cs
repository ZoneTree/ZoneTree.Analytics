using ZoneTree.Analytics.Internal;
using ZoneTree.Serializers;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The state of an active session.
/// </summary>
/// <remarks>
/// The ingestion worker that owns a visitor mutates its cached instance and writes
/// clones to ZoneTree on flush, so instances stored in ZoneTree are never modified.
/// </remarks>
sealed class SessionState
{
  public ulong SessionId;

  public long StartTicks;

  public long LastTicks;

  /// <summary>
  /// Wall-clock UTC ticks after which the stored record is treated as deleted (TTL).
  /// </summary>
  public long ExpiresAtUtcTicks;

  public int PageViews;

  public int Interactions;

  public int Events;

  public string EntryPage = "";

  public string EntryHostname = "";

  public string LastPage;

  public string LastHostname;

  public long LastPageTicks;

  /// <summary>
  /// Values of <see cref="StandardDimensions.SessionLevel"/>, in order.
  /// </summary>
  public string[] Dimensions = [];

  /// <summary>
  /// Session-level dimensions plus the entry page, built on first use. Not persisted.
  /// </summary>
  public Dictionary<string, string> DimensionCache;

  // Transient state of the owning worker. Not persisted.

  public string Site;

  public bool PendingStart;

  public ulong PendingVisitor;

  public int PendingBounces;

  public long PendingDurationTicks;

  public int PendingPageViews;

  public bool HasPendingDeltas =>
      PendingStart || PendingBounces != 0 || PendingDurationTicks != 0 || PendingPageViews != 0;

  public void ClearPendingDeltas()
  {
    PendingStart = false;
    PendingVisitor = 0;
    PendingBounces = 0;
    PendingDurationTicks = 0;
    PendingPageViews = 0;
  }

  public bool IsBounce => Interactions <= 1;

  public SessionState Clone()
  {
    var clone = (SessionState)MemberwiseClone();
    clone.Dimensions = (string[])Dimensions.Clone();
    clone.DimensionCache = null;
    return clone;
  }
}

/// <summary>
/// Binary serializer of <see cref="SessionState"/>. The type name is part of the
/// persisted ZoneTree metadata and must stay stable.
/// </summary>
sealed class SessionStateSerializer : ISerializer<SessionState>
{
  const byte Version = 1;

  const byte NullValue = 0;

  [ThreadStatic]
  static ByteWriter threadWriter;

  public SessionState Deserialize(Memory<byte> bytes)
  {
    var reader = new ByteReader(bytes.Span);
    var version = reader.ReadByte();
    if (version == NullValue)
      return null;
    if (version != Version)
      throw new FormatException($"Unsupported session state version {version}.");
    var state = new SessionState
    {
      SessionId = reader.ReadVarUInt64(),
      StartTicks = reader.ReadVarInt64()
    };
    state.LastTicks = state.StartTicks + reader.ReadVarInt64();
    state.ExpiresAtUtcTicks = reader.ReadVarInt64();
    state.PageViews = (int)reader.ReadVarInt64();
    state.Interactions = (int)reader.ReadVarInt64();
    state.Events = (int)reader.ReadVarInt64();
    state.EntryPage = reader.ReadString() ?? "";
    state.EntryHostname = reader.ReadString() ?? "";
    state.LastPage = reader.ReadString();
    state.LastHostname = reader.ReadString();
    state.LastPageTicks = state.StartTicks + reader.ReadVarInt64();
    var count = (int)reader.ReadVarUInt64();
    var dimensions = new string[StandardDimensions.SessionLevel.Count];
    for (var i = 0; i < count; ++i)
    {
      // Null means the dimension is not collected; empty means collected but unknown.
      var value = reader.ReadString();
      if (i < dimensions.Length)
        dimensions[i] = value;
    }
    state.Dimensions = dimensions;
    return state;
  }

  public Memory<byte> Serialize(in SessionState entry)
  {
    if (entry == null)
      return new byte[] { NullValue };
    var writer = threadWriter ??= new ByteWriter(256);
    writer.Reset();
    writer.WriteByte(Version);
    writer.WriteVarUInt64(entry.SessionId);
    writer.WriteVarInt64(entry.StartTicks);
    writer.WriteVarInt64(entry.LastTicks - entry.StartTicks);
    writer.WriteVarInt64(entry.ExpiresAtUtcTicks);
    writer.WriteVarInt64(entry.PageViews);
    writer.WriteVarInt64(entry.Interactions);
    writer.WriteVarInt64(entry.Events);
    writer.WriteString(entry.EntryPage);
    writer.WriteString(entry.EntryHostname);
    writer.WriteString(entry.LastPage);
    writer.WriteString(entry.LastHostname);
    writer.WriteVarInt64(entry.LastPageTicks - entry.StartTicks);
    writer.WriteVarUInt64((ulong)entry.Dimensions.Length);
    foreach (var value in entry.Dimensions)
      writer.WriteString(value);
    return writer.ToArray();
  }
}
