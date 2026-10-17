using System.Text;
using ZoneTree.Analytics.Query;

namespace ZoneTree.Analytics.Storage;

sealed class JointBackfillState
{
  public string Configuration { get; set; }
  public bool Copying { get; set; }
  public long Coverage { get; set; }
  /// <summary>The last raw event sequence the reconstruction reads; ingestion writes later events itself.</summary>
  public long Fence { get; set; }
  /// <summary>The raw partitions when the reconstruction began, which hold every event it reads.</summary>
  public string[] Partitions { get; set; } = [];
  public string Site { get; set; }
  public bool SiteCompleted { get; set; }
  public string Partition { get; set; }
  public byte[] Key { get; set; }
  public long Events { get; set; }
  public long Sessions { get; set; }
  public long Updates { get; set; }
  public long Flushes { get; set; }
  public int Sites { get; set; }
  public Dictionary<ulong, RawSession> Active { get; set; } = [];
  public List<JointBackfillReplacement> Redo { get; set; } = [];
}

sealed class JointBackfillReplacement
{
  public Granularity Granularity { get; set; }
  public string Partition { get; set; }
  public byte[] Key { get; set; }
  public byte[] Value { get; set; }
}

/// <summary>
/// The cursor, active sessions and absolute rebuilt parts are committed together. A crash
/// during the writes replays the rebuilt parts instead of applying additive contributions twice;
/// a rebuilt part is apart from what ingestion writes, so replaying it never undoes a flush.
/// </summary>
/// <remarks>
/// The journal is binary, in <see cref="AlternatingFiles"/>: a flush of replacements holds
/// serialized rollup rows, whose dense sketches are 16 KiB each.
/// </remarks>
sealed class JointBackfillCheckpoint
{
  readonly AlternatingFiles files;

  public JointBackfillState State { get; }
  public bool Resuming { get; }

  /// <summary>Opens the journal: resumes its checkpoint of the configuration, or begins a new one.</summary>
  /// <param name="root">The data directory.</param>
  /// <param name="configuration">The signature of the reconstruction.</param>
  /// <param name="begin">The state of a new reconstruction.</param>
  /// <param name="resume">False to begin a new reconstruction even when a checkpoint of the configuration remains.</param>
  public JointBackfillCheckpoint(string root, string configuration, Func<JointBackfillState> begin, bool resume = true)
  {
    var directory = Path.Combine(root, "backfill", "joint-rollups");
    Directory.CreateDirectory(directory);
    files = new(Path.Combine(directory, "journal-a.bin"), Path.Combine(directory, "journal-b.bin"), "ZTAJRNL1"u8);
    var loaded = files.Load(stream =>
    {
      using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
      return Read(reader);
    });
    Resuming = resume && loaded?.Configuration == configuration;
    if (Resuming)
      State = loaded;
    else
    {
      State = begin();
      State.Configuration = configuration;
      Save();
    }
  }

  public void Save() => files.Save(stream =>
  {
    using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    Write(writer, State);
  });

  /// <summary>
  /// Writes the rebuilt parts of the checkpoint, then commits that they are written.
  /// </summary>
  public void Replay(RollupStore rollups)
  {
    if (State.Redo.Count == 0) return;
    var serializer = new RollupValueSerializer();
    foreach (var group in State.Redo.GroupBy(row => (row.Granularity, row.Partition)))
    {
      using var lease = rollups.GetStore(group.Key.Granularity).Acquire(group.Key.Partition);
      if (lease == null) continue;
      var writer = rollups.WriterOf(group.Key.Granularity, lease.Tree);
      foreach (var row in group)
        writer.Write(new RollupWrite(row.Key, serializer.Deserialize(row.Value), RollupWriteKind.Rebuild));
    }
    State.Redo.Clear();
    Save();
  }

  public void Complete() => files.Delete();

  static void Write(BinaryWriter writer, JointBackfillState state)
  {
    writer.Write(StorageFormats.JointBackfillJournal);
    writer.Write(state.Configuration);
    writer.Write(state.Copying);
    writer.Write(state.Coverage);
    writer.Write(state.Fence);
    writer.Write(state.Partitions.Length);
    foreach (var partition in state.Partitions)
      writer.Write(partition);
    WriteString(writer, state.Site);
    writer.Write(state.SiteCompleted);
    WriteString(writer, state.Partition);
    WriteBytes(writer, state.Key);
    writer.Write(state.Events);
    writer.Write(state.Sessions);
    writer.Write(state.Updates);
    writer.Write(state.Flushes);
    writer.Write(state.Sites);
    writer.Write(state.Active.Count);
    foreach (var (id, session) in state.Active)
    {
      writer.Write(id);
      writer.Write(session.Start);
      writer.Write(session.Last);
      writer.Write(session.PageViews);
      writer.Write(session.Interactions);
      writer.Write(session.Visitor);
      writer.Write(session.Allowed);
      writer.Write(session.Dimensions?.Count ?? -1);
      if (session.Dimensions != null)
      {
        foreach (var (name, value) in session.Dimensions)
        {
          writer.Write(name);
          WriteString(writer, value);
        }
      }
      writer.Write(session.HasStartEvent);
      writer.Write(session.FirstIsPageView);
      WriteString(writer, session.FirstPage);
      WriteString(writer, session.FirstHostname);
      WriteString(writer, session.LastPage);
      WriteString(writer, session.LastHostname);
      writer.Write(session.LastPageTicks);
    }
    writer.Write(state.Redo.Count);
    foreach (var row in state.Redo)
    {
      writer.Write((int)row.Granularity);
      writer.Write(row.Partition);
      WriteBytes(writer, row.Key);
      WriteBytes(writer, row.Value);
    }
  }

  static JointBackfillState Read(BinaryReader reader)
  {
    StorageFormats.Require(reader.ReadInt32(), StorageFormats.JointBackfillJournal, "The joint rollup backfill journal");
    var state = new JointBackfillState
    {
      Configuration = reader.ReadString(),
      Copying = reader.ReadBoolean(),
      Coverage = reader.ReadInt64(),
      Fence = reader.ReadInt64(),
      Partitions = ReadStrings(reader),
      Site = ReadString(reader),
      SiteCompleted = reader.ReadBoolean(),
      Partition = ReadString(reader),
      Key = ReadBytes(reader),
      Events = reader.ReadInt64(),
      Sessions = reader.ReadInt64(),
      Updates = reader.ReadInt64(),
      Flushes = reader.ReadInt64(),
      Sites = reader.ReadInt32()
    };
    var active = reader.ReadInt32();
    for (var i = 0; i < active; ++i)
    {
      var id = reader.ReadUInt64();
      var session = new RawSession
      {
        Start = reader.ReadInt64(),
        Last = reader.ReadInt64(),
        PageViews = reader.ReadInt32(),
        Interactions = reader.ReadInt32(),
        Visitor = reader.ReadUInt64(),
        Allowed = reader.ReadBoolean()
      };
      var dimensions = reader.ReadInt32();
      if (dimensions >= 0)
      {
        session.Dimensions = new(dimensions, StringComparer.Ordinal);
        for (var j = 0; j < dimensions; ++j)
          session.Dimensions[reader.ReadString()] = ReadString(reader);
      }
      session.HasStartEvent = reader.ReadBoolean();
      session.FirstIsPageView = reader.ReadBoolean();
      session.FirstPage = ReadString(reader);
      session.FirstHostname = ReadString(reader);
      session.LastPage = ReadString(reader);
      session.LastHostname = ReadString(reader);
      session.LastPageTicks = reader.ReadInt64();
      state.Active[id] = session;
    }
    var redo = reader.ReadInt32();
    state.Redo = new(redo);
    for (var i = 0; i < redo; ++i)
    {
      state.Redo.Add(new JointBackfillReplacement
      {
        Granularity = (Granularity)reader.ReadInt32(),
        Partition = reader.ReadString(),
        Key = ReadBytes(reader),
        Value = ReadBytes(reader)
      });
    }
    return state;
  }

  static void WriteString(BinaryWriter writer, string value)
  {
    writer.Write(value != null);
    if (value != null)
      writer.Write(value);
  }

  static string ReadString(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

  static string[] ReadStrings(BinaryReader reader)
  {
    var values = new string[reader.ReadInt32()];
    for (var i = 0; i < values.Length; ++i)
      values[i] = reader.ReadString();
    return values;
  }

  static void WriteBytes(BinaryWriter writer, byte[] value)
  {
    writer.Write(value?.Length ?? -1);
    if (value != null)
      writer.Write(value);
  }

  static byte[] ReadBytes(BinaryReader reader)
  {
    var length = reader.ReadInt32();
    return length < 0 ? null : reader.ReadBytes(length);
  }
}
