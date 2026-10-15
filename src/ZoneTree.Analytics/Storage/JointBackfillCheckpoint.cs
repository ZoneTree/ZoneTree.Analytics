using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ZoneTree.Analytics.Query;

namespace ZoneTree.Analytics.Storage;

sealed class JointBackfillState
{
  public string Configuration { get; set; }
  public bool Copying { get; set; }
  public long Coverage { get; set; }
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
/// The cursor, active sessions and absolute replacements are committed together. A crash
/// during the writes replays replacements instead of applying additive contributions twice.
/// </summary>
/// <remarks>
/// The journal is binary: a flush of replacements holds serialized rollup rows, whose dense
/// sketches are 16 KiB each. Two files are written alternately, like <see cref="MetaStore"/>,
/// so a crash can only damage the file being written; loading picks the valid file with the
/// highest generation. File layout:
/// <code>
/// magic(8) generation(8, LE) body length(8, LE) sha256 of body(32) body
/// </code>
/// </remarks>
sealed class JointBackfillCheckpoint
{
  const int Version = 1;

  const int HeaderLength = 56;

  static readonly byte[] Magic = "ZTAJRNL1"u8.ToArray();

  readonly string[] paths;

  long generation;

  public JointBackfillState State { get; }
  public bool Resuming { get; }

  public JointBackfillCheckpoint(string root, string configuration, long coverage)
  {
    var directory = Path.Combine(root, "backfill", "joint-rollups");
    Directory.CreateDirectory(directory);
    paths = [Path.Combine(directory, "journal-a.bin"), Path.Combine(directory, "journal-b.bin")];
    var loaded = Load();
    Resuming = loaded?.Configuration == configuration;
    State = Resuming ? loaded : new() { Configuration = configuration, Coverage = coverage };
    if (!Resuming) Save();
  }

  public void Save()
  {
    var path = paths[(generation + 1) % 2];
    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
    {
      // The header is written last: a torn write never carries a matching checksum.
      stream.Write(new byte[HeaderLength]);
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      using (var body = new BinaryWriter(new HashingStream(stream, hash), Encoding.UTF8, leaveOpen: true))
        Write(body, State);
      var length = stream.Position - HeaderLength;
      var header = new byte[HeaderLength];
      Magic.CopyTo(header, 0);
      BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), generation + 1);
      BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), length);
      hash.GetHashAndReset().CopyTo(header, 24);
      stream.Position = 0;
      stream.Write(header);
      stream.Flush(flushToDisk: true);
    }
    ++generation;
  }

  public void Replay(RollupStore rollups)
  {
    if (State.Redo.Count == 0) return;
    var serializer = new RollupValueSerializer();
    foreach (var group in State.Redo.GroupBy(row => (row.Granularity, row.Partition)))
    {
      using var lease = rollups.GetStore(group.Key.Granularity).Acquire(group.Key.Partition);
      if (lease == null) continue;
      foreach (var row in group)
        lease.Tree.Upsert(row.Key, serializer.Deserialize(row.Value));
    }
    IndexRecoveryScan.Persist(rollups.PrimaryStores);
    State.Redo.Clear();
    Save();
  }

  public void Complete()
  {
    foreach (var path in paths)
      File.Delete(path);
    generation = 0;
  }

  JointBackfillState Load()
  {
    JointBackfillState newest = null;
    foreach (var path in paths)
    {
      if (!File.Exists(path))
        continue;
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
      if (TryReadHeader(stream, out var fileGeneration) && fileGeneration > generation)
      {
        stream.Position = HeaderLength;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        newest = Read(reader);
        generation = fileGeneration;
      }
    }
    return newest;
  }

  /// <summary>Validates the header and the checksum of the whole body.</summary>
  static bool TryReadHeader(FileStream stream, out long fileGeneration)
  {
    fileGeneration = 0;
    var header = new byte[HeaderLength];
    if (stream.Length < HeaderLength || stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) != HeaderLength ||
        !header.AsSpan(0, 8).SequenceEqual(Magic))
      return false;
    fileGeneration = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
    var length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
    if (fileGeneration < 1 || length != stream.Length - HeaderLength)
      return false;
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = new byte[1 << 16];
    int read;
    while ((read = stream.Read(buffer)) > 0)
      hash.AppendData(buffer, 0, read);
    return hash.GetHashAndReset().AsSpan().SequenceEqual(header.AsSpan(24, 32));
  }

  static void Write(BinaryWriter writer, JointBackfillState state)
  {
    writer.Write(Version);
    writer.Write(state.Configuration);
    writer.Write(state.Copying);
    writer.Write(state.Coverage);
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
    if (reader.ReadInt32() != Version)
      throw new AnalyticsConfigurationException("The joint rollup backfill journal has an unknown version.");
    var state = new JointBackfillState
    {
      Configuration = reader.ReadString(),
      Copying = reader.ReadBoolean(),
      Coverage = reader.ReadInt64(),
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

  /// <summary>Forwards writes to a stream and hashes them.</summary>
  sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
  {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
      hash.AppendData(buffer);
      inner.Write(buffer);
    }
  }
}
