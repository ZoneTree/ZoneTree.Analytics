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
/// hides them from every reader at once, merges remove them from disk and drop disk segment
/// parts that expired as a whole without reading them (see <see cref="ExpiredRecords"/>), and
/// partitions that expire as a whole are dropped.
/// </remarks>
sealed class StoreFactory
{
  readonly StorageOptions storage;

  readonly string dataRoot;

  readonly string writeAheadLogRoot;

  readonly ILogger logger;

  readonly RetentionClock retention;

  public bool RawEventsDurable { get; private set; } = true;

  public bool DimensionIndexesDurable { get; private set; } = true;

  public bool RollupIndexesDurable { get; private set; } = true;

  public bool RollupsDurable { get; private set; } = true;

  public long RawRetentionCutoff => retention.Current.RawEvents;

  public long RollupRetentionCutoff(Granularity granularity) => retention.Current.RollupCutoff(granularity);

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
          if (store.EnableSingleSegmentGarbageCollection is { } collect)
            options.EnableSingleSegmentGarbageCollection = collect;
          options.AllowUnsafeOptionValues = store.AllowUnsafeOptionValues;
        });
    if (store.MutableSegmentMaxItemCount is { } mutableSegmentMaxItemCount)
      factory.SetMutableSegmentMaxItemCount(mutableSegmentMaxItemCount);
    if (store.ScannedOnly)
      factory.SetMutableSegmentBloomFilterBitsPerItem(0);
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
    {
      var factory = RawEventsFactory(null);
      factory.Validate();
      RawEventsDurable = factory.Options.WriteAheadLogOptions.WriteAheadLogMode != WriteAheadLogMode.None;
    }
    if (storage.DimensionIndexes.Dimensions.Count > 0)
    {
      var factory = DimensionIndexFactory(null);
      factory.Validate();
      DimensionIndexesDurable = factory.Options.WriteAheadLogOptions.WriteAheadLogMode != WriteAheadLogMode.None;
    }
    var rollupFactory = RollupsFactory(null, Granularity.Hour);
    rollupFactory.Validate();
    RollupsDurable = rollupFactory.Options.WriteAheadLogOptions.WriteAheadLogMode != WriteAheadLogMode.None;
    if (storage.RollupIndexes.Definitions.Count > 0)
    {
      var indexFactory = RollupIndexFactory(null, Granularity.Hour);
      indexFactory.Validate();
      RollupIndexesDurable = indexFactory.Options.WriteAheadLogOptions.WriteAheadLogMode != WriteAheadLogMode.None;
    }
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

  public IMaintainer CreateDimensionIndexMaintainer(IZoneTree<Memory<byte>, Memory<byte>> tree) =>
      CreateMaintainer(tree, storage.DimensionIndexes.Store);

  public IZoneTree<Memory<byte>, Memory<byte>> OpenDimensionIndex(string directory) =>
      DimensionIndexFactory(directory).OpenOrCreate();

  ZoneTreeFactory<Memory<byte>, Memory<byte>> DimensionIndexFactory(string directory)
  {
    ExpiredKeyPredicate expired = retention.IsRawEventExpired;
    return Create<Memory<byte>, Memory<byte>>(directory, storage.DimensionIndexes.Store)
        .SetComparer(new ByteArrayLexicographicComparerAscending())
        .SetIsDeletedDelegate((in Memory<byte> key, in Memory<byte> value) => value.Length == 0 || expired(key.Span))
        .SetIsRangeDeletedDelegate((in Memory<byte> first, in Memory<byte> _, in Memory<byte> last, in Memory<byte> _) =>
            ExpiredRecords.IsRangeExpired(first.Span, last.Span, DimensionIndexKeyCodec.HeadLength, expired))
        .SetMarkValueDeletedDelegate((ref Memory<byte> value) => value = Memory<byte>.Empty);
  }

  public IMaintainer CreateRollupsMaintainer(IZoneTree<Memory<byte>, RollupValue> tree) =>
      CreateMaintainer(tree, storage.Rollups);

  public IMaintainer CreateRollupIndexMaintainer(IZoneTree<Memory<byte>, RollupValue> tree) =>
      CreateMaintainer(tree, storage.RollupIndexes.Store);

  public IZoneTree<Memory<byte>, RollupValue> OpenRollupIndex(string directory, Granularity granularity) =>
      RollupIndexFactory(directory, granularity).OpenOrCreate();

  ZoneTreeFactory<Memory<byte>, RollupValue> RollupIndexFactory(string directory, Granularity granularity)
  {
    ExpiredKeyPredicate expired = key => retention.IsRollupExpired(granularity, RollupIndexKeyCodec.OriginalKey(key));
    return Create<Memory<byte>, RollupValue>(directory, storage.RollupIndexes.Store)
        .SetComparer(new ByteArrayLexicographicComparerAscending())
        .SetValueSerializer(new RollupValueSerializer())
        .SetIsDeletedDelegate((in Memory<byte> key, in RollupValue value) => value == null || expired(key.Span))
        .SetIsRangeDeletedDelegate((in Memory<byte> first, in RollupValue _, in Memory<byte> last, in RollupValue _) =>
            ExpiredRecords.IsRangeExpired(first.Span, last.Span, RollupIndexKeyCodec.HeadLength, expired))
        .SetMarkValueDeletedDelegate((ref RollupValue value) => value = null);
  }

  public IMaintainer CreateSessionsMaintainer(IZoneTree<ulong, SessionState> tree) =>
      CreateMaintainer(tree, storage.Sessions);

  /// <summary>
  /// Raw events: Memory&lt;byte&gt; keys and values.
  /// </summary>
  public IZoneTree<Memory<byte>, Memory<byte>> OpenRawEvents(string directory) =>
      RawEventsFactory(directory).OpenOrCreate();

  ZoneTreeFactory<Memory<byte>, Memory<byte>> RawEventsFactory(string directory)
  {
    ExpiredKeyPredicate isExpired = retention.IsRawEventExpired;
    // A deleted event has an empty value; encoded events are never empty.
    return Create<Memory<byte>, Memory<byte>>(directory, storage.RawEvents)
        .SetComparer(new ByteArrayLexicographicComparerAscending())
        .SetIsDeletedDelegate((in Memory<byte> key, in Memory<byte> value) =>
            value.Length == 0 || isExpired(key.Span))
        .SetIsRangeDeletedDelegate((in Memory<byte> first, in Memory<byte> _, in Memory<byte> last, in Memory<byte> _) =>
            ExpiredRecords.IsRangeExpired(first.Span, last.Span, KeyCodec.RawEventHead, isExpired))
        .SetMarkValueDeletedDelegate((ref Memory<byte> value) => value = Memory<byte>.Empty);
  }

  /// <summary>
  /// Rollups of a granularity: order-preserving binary keys, <see cref="RollupValue"/> values.
  /// </summary>
  public IZoneTree<Memory<byte>, RollupValue> OpenRollups(string directory, Granularity granularity) =>
      RollupsFactory(directory, granularity).OpenOrCreate();

  ZoneTreeFactory<Memory<byte>, RollupValue> RollupsFactory(string directory, Granularity granularity)
  {
    ExpiredKeyPredicate isExpired = key => retention.IsRollupExpired(granularity, key);
    return Create<Memory<byte>, RollupValue>(directory, storage.Rollups)
        .SetComparer(new ByteArrayLexicographicComparerAscending())
        .SetValueSerializer(new RollupValueSerializer())
        .SetIsDeletedDelegate((in Memory<byte> key, in RollupValue value) =>
            value == null || isExpired(key.Span))
        .SetIsRangeDeletedDelegate((in Memory<byte> first, in RollupValue _, in Memory<byte> last, in RollupValue _) =>
            ExpiredRecords.IsRangeExpired(first.Span, last.Span, KeyCodec.RollupHead, isExpired))
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
