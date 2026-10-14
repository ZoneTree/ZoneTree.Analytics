using System.Text.Json;
using System.Text.Json.Serialization;
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

[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(JointBackfillState))]
partial class JointBackfillJson : JsonSerializerContext;

/// <summary>
/// The cursor, active sessions and absolute replacements are committed together. A crash
/// during the writes replays replacements instead of applying additive contributions twice.
/// Dedicated two-generation metadata files keep this temporary journal out of engine metadata.
/// </summary>
sealed class JointBackfillCheckpoint
{
  readonly MetaStore meta;

  public JointBackfillState State { get; }
  public bool Resuming { get; }

  public JointBackfillCheckpoint(string root, string configuration, long coverage)
  {
    meta = new MetaStore(Path.Combine(root, "backfill", "joint-rollups"));
    var json = meta.Get("state");
    var loaded = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, JointBackfillJson.Default.JointBackfillState);
    Resuming = loaded?.Configuration == configuration;
    State = Resuming ? loaded : new() { Configuration = configuration, Coverage = coverage };
    if (!Resuming) Save();
  }

  public void Save() => meta.Set("state", JsonSerializer.Serialize(State, JointBackfillJson.Default.JointBackfillState));

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

  public void Complete() => meta.Delete("state");
}
