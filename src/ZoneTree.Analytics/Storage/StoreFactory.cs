using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Creates the ZoneTree instances of every store with consistent configuration.
/// </summary>
sealed class StoreFactory
{
  readonly StorageOptions storage;

  readonly ILogger logger;

  public StoreFactory(StorageOptions storage, ILogger logger)
  {
    this.storage = storage;
    this.logger = logger;
  }

  ZoneTreeFactory<TKey, TValue> Create<TKey, TValue>(
      string directory, WriteAheadLogMode walMode)
  {
    // The logger must be set before the data directory: the device manager captures it.
    return new ZoneTreeFactory<TKey, TValue>()
        .SetLogger(logger)
        .SetDataDirectory(directory)
        .ConfigureWriteAheadLogOptions(options =>
        {
          options.WriteAheadLogMode = walMode;
          storage.ConfigureWriteAheadLog?.Invoke(options);
        })
        .ConfigureDiskSegmentOptions(options =>
        {
          storage.ConfigureDiskSegments?.Invoke(options);
        });
  }

  /// <summary>
  /// Raw events: Memory&lt;byte&gt; keys and values. Raw events are only scanned, never
  /// point-read, so the mutable segment Bloom filter is disabled.
  /// </summary>
  public IZoneTree<Memory<byte>, Memory<byte>> OpenRawEvents(string directory)
  {
    return Create<Memory<byte>, Memory<byte>>(directory, storage.WriteAheadLogMode)
        .SetComparer(new LexicographicByteComparer())
        .SetMutableSegmentMaxItemCount(storage.RawEventsMutableSegmentMaxItemCount)
        .SetMutableSegmentBloomFilterBitsPerItem(0)
        // An empty value is the deletion marker. Set explicitly: encoded events are never empty.
        .SetIsDeletedDelegate((in Memory<byte> _, in Memory<byte> value) => value.Length == 0)
        .SetMarkValueDeletedDelegate((ref Memory<byte> value) => value = Memory<byte>.Empty)
        .OpenOrCreate();
  }

  /// <summary>
  /// Rollups: order-preserving binary keys, <see cref="RollupValue"/> values.
  /// </summary>
  public IZoneTree<Memory<byte>, RollupValue> OpenRollups(string directory)
  {
    return Create<Memory<byte>, RollupValue>(directory, storage.WriteAheadLogMode)
        .SetComparer(new LexicographicByteComparer())
        .SetMutableSegmentMaxItemCount(storage.RollupsMutableSegmentMaxItemCount)
        .SetValueSerializer(new RollupValueSerializer())
        .SetIsDeletedDelegate((in Memory<byte> _, in RollupValue value) => value == null)
        .SetMarkValueDeletedDelegate((ref RollupValue value) => value = null)
        .OpenOrCreate();
  }

  /// <summary>
  /// Active sessions keyed by visitor hash. Expired sessions are hidden by a TTL
  /// deletion predicate and physically removed by normal merges.
  /// </summary>
  public IZoneTree<ulong, SessionState> OpenSessions(string directory)
  {
    return Create<ulong, SessionState>(directory, storage.WriteAheadLogMode)
        .SetMutableSegmentMaxItemCount(50_000)
        .SetValueSerializer(new SessionStateSerializer())
        .SetIsDeletedDelegate((in ulong _, in SessionState value) =>
            value == null || value.ExpiresAtUtcTicks <= DateTime.UtcNow.Ticks)
        .SetMarkValueDeletedDelegate((ref SessionState value) => value = null)
        .OpenOrCreate();
  }

  /// <summary>
  /// Engine metadata: configuration, secrets and salts. Rarely written and must be
  /// durable, so it uses the synchronous WAL.
  /// </summary>
  public IZoneTree<string, string> OpenMeta(string directory)
  {
    return Create<string, string>(directory, WriteAheadLogMode.Sync)
        .SetMutableSegmentMaxItemCount(1_000)
        .OpenOrCreate();
  }
}

/// <summary>
/// Engine metadata store.
/// </summary>
sealed class MetaStore : IDisposable
{
  readonly IZoneTree<string, string> tree;

  readonly IMaintainer maintainer;

  public MetaStore(IZoneTree<string, string> tree)
  {
    this.tree = tree;
    maintainer = tree.CreateMaintainer();
  }

  public string Get(string key) => tree.TryGet(key, out var value) ? value : null;

  public void Set(string key, string value) => tree.Upsert(key, value);

  public void Delete(string key) => tree.ForceDelete(key);

  public List<KeyValuePair<string, string>> ListByPrefix(string prefix)
  {
    var result = new List<KeyValuePair<string, string>>();
    using var iterator = tree.CreateIterator();
    iterator.Seek(prefix);
    while (iterator.Next())
    {
      if (!iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
        break;
      result.Add(iterator.Current);
    }
    return result;
  }

  public void Dispose()
  {
    maintainer.Dispose();
    tree.Dispose();
  }
}
