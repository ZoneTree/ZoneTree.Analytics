using ZoneTree.Analytics.Ingestion;
using ZoneTree.Comparers;
using ZoneTree.Logger;
using ZoneTree.Options;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Creates the ZoneTree instances of every store: the engine chooses comparers, serializers and
/// deletion predicates (the storage format), <see cref="StoreOptions"/> everything else. Byte keys
/// use <see cref="ByteArrayLexicographicComparerAscending"/>: the prefix seeks of
/// <see cref="KeyCodec"/> rely on a key sorting before every longer key it is a prefix of.
/// </summary>
/// <remarks>
/// Raw events and rollup rows older than retention are deleted by the stores' deletion
/// predicates, which compare the time in the key with the <see cref="RetentionClock"/>: retention
/// hides them from every reader at once, merges remove them from disk (see
/// <see cref="ExpiredRecords"/>), and partitions that expire as a whole are dropped.
/// </remarks>
sealed class StoreFactory
{
  readonly StorageOptions storage;

  readonly string dataRoot;

  readonly string writeAheadLogRoot;

  readonly ILogger logger;

  readonly RetentionClock retention;

  public StoreFactory(StorageOptions storage, string dataRoot, ILogger logger, RetentionClock retention)
  {
    this.storage = storage;
    this.retention = retention;
    this.dataRoot = dataRoot;
    writeAheadLogRoot = storage.WriteAheadLogDirectory == null ? null : Path.GetFullPath(storage.WriteAheadLogDirectory);
    this.logger = logger;
  }

  /// <summary>
  /// The write-ahead-log directory of a store, or null when logs are kept next to the data.
  /// </summary>
  public string WriteAheadLogDirectoryOf(string dataDirectory) =>
      writeAheadLogRoot == null || dataDirectory == null ? null : Path.Combine(writeAheadLogRoot, Path.GetRelativePath(dataRoot, dataDirectory));

  /// <summary>
  /// A factory configured from the store options. Without a directory it opens nothing and only
  /// serves <see cref="Validate"/>.
  /// </summary>
  ZoneTreeFactory<TKey, TValue> Create<TKey, TValue>(string directory, StoreOptions store)
  {
    // The logger must be set before the data directory: the device manager captures it.
    var factory = new ZoneTreeFactory<TKey, TValue>()
        .SetLogger(logger)
        .ConfigureWriteAheadLogOptions(options =>
        {
          options.WriteAheadLogMode = store.WriteAheadLogMode;
          store.ConfigureWriteAheadLog?.Invoke(options);
        })
        .ConfigureDiskSegmentOptions(options => store.ConfigureDiskSegments?.Invoke(options))
        .Configure(options =>
        {
          if (store.BTreeLockMode is { } lockMode)
            options.BTreeLockMode = lockMode;
          if (store.BTreeNodeSize is { } nodeSize)
            options.BTreeNodeSize = nodeSize;
          if (store.BTreeLeafSize is { } leafSize)
            options.BTreeLeafSize = leafSize;
          if (store.EnableSingleSegmentGarbageCollection is { } collect)
            options.EnableSingleSegmentGarbageCollection = collect;
          options.AllowUnsafeOptionValues = store.AllowUnsafeOptionValues;
        });
    if (store.MutableSegmentMaxItemCount is { } mutableSegmentMaxItemCount)
      factory.SetMutableSegmentMaxItemCount(mutableSegmentMaxItemCount);
    if (store.MutableSegmentBloomFilterBitsPerItem is { } bitsPerItem)
      factory.SetMutableSegmentBloomFilterBitsPerItem(bitsPerItem);
    if (store.DiskSegmentMaxItemCount is { } diskSegmentMaxItemCount)
      factory.SetDiskSegmentMaxItemCount(diskSegmentMaxItemCount);
    if (directory != null)
      factory.SetDataDirectory(directory);
    if (WriteAheadLogDirectoryOf(directory) is { } walDirectory)
      factory.SetWriteAheadLogDirectory(walDirectory);
    return factory;
  }

  /// <summary>
  /// Validates the ZoneTree settings of every store without creating or opening anything, so
  /// invalid <see cref="StoreOptions"/> fail at engine start rather than on the first event.
  /// </summary>
  public void Validate(SessionSchema schema)
  {
    if (storage.StoreRawEvents)
      RawEventsFactory(null).Validate();
    RollupsFactory(null, Granularity.Hour).Validate();
    SessionsFactory(null, schema).Validate();
  }

  /// <summary>
  /// Creates the maintainer of a tree, which also merges bottom segments (see
  /// <see cref="StoreMaintainer{TKey, TValue}"/>), and applies <see cref="StoreOptions.ConfigureMaintainer"/>.
  /// </summary>
  static StoreMaintainer<TKey, TValue> CreateMaintainer<TKey, TValue>(IZoneTree<TKey, TValue> tree, StoreOptions store)
  {
    var maintainer = new StoreMaintainer<TKey, TValue>(tree);
    try
    {
      store.ConfigureMaintainer?.Invoke(maintainer);
    }
    catch
    {
      maintainer.Dispose();
      throw;
    }
    return maintainer;
  }

  public IMaintainer CreateRawEventsMaintainer(IZoneTree<Memory<byte>, Memory<byte>> tree) =>
      CreateMaintainer(tree, storage.RawEvents);

  public IMaintainer CreateRollupsMaintainer(IZoneTree<Memory<byte>, RollupValue> tree) =>
      CreateMaintainer(tree, storage.Rollups);

  public IMaintainer CreateSessionsMaintainer(IZoneTree<ulong, SessionState> tree) =>
      CreateMaintainer(tree, storage.Sessions);

  /// <summary>
  /// Raw events: Memory&lt;byte&gt; keys and values.
  /// </summary>
  public IZoneTree<Memory<byte>, Memory<byte>> OpenRawEvents(string directory) =>
      RawEventsFactory(directory).OpenOrCreate();

  ZoneTreeFactory<Memory<byte>, Memory<byte>> RawEventsFactory(string directory)
  {
    // A deleted event has an empty value; encoded events are never empty.
    return Create<Memory<byte>, Memory<byte>>(directory, storage.RawEvents)
        .SetComparer(new ByteArrayLexicographicComparerAscending())
        .SetIsDeletedDelegate((in Memory<byte> key, in Memory<byte> value) =>
            value.Length == 0 || retention.IsRawEventExpired(key.Span))
        .SetMarkValueDeletedDelegate((ref Memory<byte> value) => value = Memory<byte>.Empty);
  }

  /// <summary>
  /// Rollups of a granularity: order-preserving binary keys, <see cref="RollupValue"/> values.
  /// </summary>
  public IZoneTree<Memory<byte>, RollupValue> OpenRollups(string directory, Granularity granularity) =>
      RollupsFactory(directory, granularity).OpenOrCreate();

  ZoneTreeFactory<Memory<byte>, RollupValue> RollupsFactory(string directory, Granularity granularity)
  {
    return Create<Memory<byte>, RollupValue>(directory, storage.Rollups)
        .SetComparer(new ByteArrayLexicographicComparerAscending())
        .SetValueSerializer(new RollupValueSerializer())
        .SetIsDeletedDelegate((in Memory<byte> key, in RollupValue value) =>
            value == null || retention.IsRollupExpired(granularity, key.Span))
        .SetMarkValueDeletedDelegate((ref RollupValue value) => value = null);
  }

  /// <summary>
  /// Active sessions keyed by visitor hash. Expired sessions are hidden by a TTL
  /// deletion predicate and physically removed by normal merges.
  /// </summary>
  public IZoneTree<ulong, SessionState> OpenSessions(string directory, SessionSchema schema) =>
      SessionsFactory(directory, schema).OpenOrCreate();

  ZoneTreeFactory<ulong, SessionState> SessionsFactory(string directory, SessionSchema schema)
  {
    return Create<ulong, SessionState>(directory, storage.Sessions)
        .SetValueSerializer(new SessionStateSerializer(schema))
        .SetIsDeletedDelegate((in ulong _, in SessionState value) =>
            value == null || value.ExpiresAtUtcTicks <= DateTime.UtcNow.Ticks)
        .SetMarkValueDeletedDelegate((ref SessionState value) => value = null);
  }
}
