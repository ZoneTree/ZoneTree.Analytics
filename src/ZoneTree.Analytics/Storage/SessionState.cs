using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Serializers;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The state of an active session.
/// </summary>
/// <remarks>
/// The ingestion worker that owns a visitor mutates its cached instance and writes
/// clones to ZoneTree on flush, so instances stored in ZoneTree are never modified.
/// Advanced callers must also treat stored instances as immutable and write a <see cref="Clone"/>.
/// </remarks>
public sealed class SessionState
{
  /// <summary>The session identifier.</summary>
  public ulong SessionId;

  /// <summary>The UTC ticks of the first event.</summary>
  public long StartTicks;

  /// <summary>The UTC ticks of the most recent event.</summary>
  public long LastTicks;

  /// <summary>
  /// Wall-clock UTC ticks after which the stored record is treated as deleted (TTL).
  /// </summary>
  public long ExpiresAtUtcTicks;

  /// <summary>The number of page views.</summary>
  public int PageViews;

  /// <summary>The number of interactions used to determine whether the session bounced.</summary>
  public int Interactions;

  /// <summary>The number of events.</summary>
  public int Events;

  /// <summary>The entry page path.</summary>
  public string EntryPage = "";

  /// <summary>The entry page hostname.</summary>
  public string EntryHostname = "";

  /// <summary>The most recent page path.</summary>
  public string LastPage;

  /// <summary>The most recent page hostname.</summary>
  public string LastHostname;

  /// <summary>The UTC ticks of the most recent page view.</summary>
  public long LastPageTicks;

  /// <summary>
  /// Session-level values, aligned with the engine's session schema: standard dimensions first, then custom ones.
  /// </summary>
  public string[] Dimensions = [];

  // Transient state of the owning worker. Not persisted.

  internal string Site;

  internal bool PendingStart;

  internal ulong PendingVisitor;

  internal int PendingBounces;

  internal long PendingDurationTicks;

  internal int PendingPageViews;

  internal bool HasPendingDeltas =>
      PendingStart || PendingBounces != 0 || PendingDurationTicks != 0 || PendingPageViews != 0;

  internal void ClearPendingDeltas()
  {
    PendingStart = false;
    PendingVisitor = 0;
    PendingBounces = 0;
    PendingDurationTicks = 0;
    PendingPageViews = 0;
  }

  /// <summary>Whether the session has at most one interaction.</summary>
  public bool IsBounce => Interactions <= 1;

  /// <summary>Creates a copy with its own dimensions array.</summary>
  public SessionState Clone()
  {
    var clone = (SessionState)MemberwiseClone();
    clone.Dimensions = (string[])Dimensions.Clone();
    return clone;
  }
}

/// <summary>
/// Binary serializer of <see cref="SessionState"/>. The type name is part of the
/// persisted ZoneTree metadata and must stay stable.
/// </summary>
/// <remarks>
/// Standard session-level values are stored by position and custom ones by name, so the custom
/// session dimensions of <see cref="SessionOptions.Dimensions"/> can change between runs while
/// sessions are still active.
/// </remarks>
sealed class SessionStateSerializer : ISerializer<SessionState>
{
  const byte Deleted = 0;

  const byte Present = 1;

  [ThreadStatic]
  static ByteWriter threadWriter;

  readonly SessionSchema schema;

  public SessionStateSerializer(SessionSchema schema)
  {
    this.schema = schema;
  }

  public SessionState Deserialize(Memory<byte> bytes)
  {
    var reader = new ByteReader(bytes.Span);
    var header = reader.ReadByte();
    // A header or trailing bytes this reader does not know are a layout it cannot read.
    if (header == Deleted && reader.Remaining == 0)
      return null;
    if (header != Present)
      throw new FormatException("The session state has an unknown layout.");
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
    state.EntryPage = reader.ReadString();
    state.EntryHostname = reader.ReadString();
    state.LastPage = reader.ReadString();
    state.LastHostname = reader.ReadString();
    state.LastPageTicks = state.StartTicks + reader.ReadVarInt64();
    var dimensions = new string[schema.Count];
    // Null means the dimension is not collected; empty means collected but unknown.
    for (var i = 0; i < SessionSchema.StandardCount; ++i)
      dimensions[i] = reader.ReadString();
    var customCount = (int)reader.ReadVarUInt64();
    for (var i = 0; i < customCount; ++i)
    {
      var name = reader.ReadString();
      var value = reader.ReadString();
      var index = schema.IndexOf(name);
      if (index >= SessionSchema.StandardCount)
        dimensions[index] = value;
    }
    if (reader.Remaining != 0)
      throw new FormatException("The session state has trailing bytes.");
    state.Dimensions = dimensions;
    return state;
  }

  public Memory<byte> Serialize(in SessionState entry)
  {
    if (entry == null)
      return new byte[] { Deleted };
    var writer = ByteWriter.Reuse(ref threadWriter, 256);
    writer.WriteByte(Present);
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
    // Positions missing from a shorter array, such as the default one, were not collected.
    var dimensions = entry.Dimensions ?? [];
    for (var i = 0; i < SessionSchema.StandardCount; ++i)
      writer.WriteString(i < dimensions.Length ? dimensions[i] : null);
    var customCount = 0;
    for (var i = SessionSchema.StandardCount; i < dimensions.Length; ++i)
    {
      if (dimensions[i] != null)
        ++customCount;
    }
    writer.WriteVarUInt64((ulong)customCount);
    for (var i = SessionSchema.StandardCount; i < dimensions.Length; ++i)
    {
      if (dimensions[i] == null)
        continue;
      writer.WriteString(schema.Names[i]);
      writer.WriteString(dimensions[i]);
    }
    return writer.ToArray();
  }
}
