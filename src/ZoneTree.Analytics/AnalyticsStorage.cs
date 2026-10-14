using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics;

/// <summary>
/// Advanced access to the ZoneTrees and maintainers owned by an <see cref="AnalyticsEngine"/>.
/// </summary>
/// <remarks>
/// Access remains valid only while the engine is open. Do not dispose or drop trees, dispose
/// maintainers, or create additional maintainers. Direct writes bypass the analytics pipeline
/// and can make raw events, rollups and cached sessions inconsistent. Treat stored values and
/// their arrays and sketches as immutable; construct replacements when writing.
/// Metadata under <c>meta/</c> uses files rather than ZoneTree and is not exposed here.
/// </remarks>
public sealed class AnalyticsStorage
{
  readonly Action ensureOpen;

  readonly IZoneTree<ulong, SessionState> sessions;

  readonly IMaintainer sessionsMaintainer;

  readonly DimensionIndexStore dimensionIndexes;

  readonly RollupIndexStore rollupIndexes;

  readonly AnalyticsPartitionedStorage<Memory<byte>, RollupValue>[] rollups = new AnalyticsPartitionedStorage<Memory<byte>, RollupValue>[3];

  internal AnalyticsStorage(
      PartitionedStore<Memory<byte>, Memory<byte>> rawEvents,
      RollupStore rollups,
      IZoneTree<ulong, SessionState> sessions,
      IMaintainer sessionsMaintainer,
      Action ensureOpen, DimensionIndexStore dimensionIndexes = null, RollupIndexStore rollupIndexes = null)
  {
    this.ensureOpen = ensureOpen;
    this.sessions = sessions;
    this.sessionsMaintainer = sessionsMaintainer;
    this.dimensionIndexes = dimensionIndexes;
    this.rollupIndexes = rollupIndexes;
    if (rawEvents != null)
      RawEvents = new(rawEvents, ensureOpen);
    for (var i = 0; i < RollupStore.Granularities.Length; ++i)
    {
      if (rollups.GetStore(RollupStore.Granularities[i]) is { } store)
        this.rollups[i] = new(store, ensureOpen);
    }
  }

  /// <summary>Raw event partitions, or null when raw event storage is disabled.</summary>
  public AnalyticsPartitionedStorage<Memory<byte>, Memory<byte>> RawEvents { get; }

  /// <summary>Configured covering dimension names, empty when no indexes are enabled.</summary>
  public IReadOnlyList<string> IndexedDimensions
  {
    get { ensureOpen(); return dimensionIndexes?.Indexes.Select(x => x.Dimension).ToArray() ?? []; }
  }

  /// <summary>
  /// Access to the shared store containing a configured covering dimension index, or null when
  /// it is disabled. Use the returned key prefix to restrict iterators to that logical index.
  /// All configured dimensions share the same partitions.
  /// </summary>
  public AnalyticsPartitionedStorage<Memory<byte>, Memory<byte>> GetDimensionIndex(string dimension)
  {
    ensureOpen();
    var index = dimensionIndexes?.Indexes.FirstOrDefault(x => x.Dimension == dimension);
    return index == null ? null : new(index.Store, ensureOpen, index.KeyPrefix.ToArray());
  }

  /// <summary>
  /// Access to the shared tier store containing a configured covering rollup index, or null
  /// when it is disabled. Use the returned <see cref="AnalyticsPartitionedStorage{TKey, TValue}.KeyPrefix"/>
  /// to restrict an iterator to that logical index. All indexes of the tier share its partitions.
  /// </summary>
  public AnalyticsPartitionedStorage<Memory<byte>, RollupValue> GetRollupIndex(
      Granularity granularity, string dimension, params string[] groupDimensions)
  {
    ensureOpen();
    ArgumentNullException.ThrowIfNull(groupDimensions);
    var definition = new RollupDefinition([dimension, .. groupDimensions]);
    var index = rollupIndexes?.Indexes.FirstOrDefault(index => index.Dimension == dimension &&
        index.DefinitionId == definition.Id && index.Granularity == granularity);
    return index == null ? null : new(index.Store, ensureOpen, index.KeyPrefix.ToArray());
  }

  /// <summary>The active session ZoneTree, or null when sessions are disabled.</summary>
  public IZoneTree<ulong, SessionState> Sessions
  {
    get
    {
      ensureOpen();
      return sessions;
    }
  }

  /// <summary>The existing session maintainer, or null when sessions are disabled.</summary>
  public IMaintainer SessionsMaintainer
  {
    get
    {
      ensureOpen();
      return sessionsMaintainer;
    }
  }

  /// <summary>
  /// Returns the partitions of one rollup granularity, or null when it is disabled.
  /// </summary>
  /// <param name="granularity">Exactly one of Minute, Hour or Day.</param>
  public AnalyticsPartitionedStorage<Memory<byte>, RollupValue> GetRollups(Granularity granularity)
  {
    ensureOpen();
    return rollups[granularity switch
    {
      Granularity.Minute => 0,
      Granularity.Hour => 1,
      Granularity.Day => 2,
      _ => throw new ArgumentOutOfRangeException(nameof(granularity))
    }];
  }
}

/// <summary>
/// Opens leases on the existing partitions of one analytics data tier.
/// </summary>
/// <typeparam name="TKey">The stored key type.</typeparam>
/// <typeparam name="TValue">The stored value type.</typeparam>
public sealed class AnalyticsPartitionedStorage<TKey, TValue>
{
  readonly PartitionedStore<TKey, TValue> store;

  readonly Action ensureOpen;

  internal AnalyticsPartitionedStorage(PartitionedStore<TKey, TValue> store, Action ensureOpen, ReadOnlyMemory<byte> keyPrefix = default)
  {
    this.store = store;
    this.ensureOpen = ensureOpen;
    KeyPrefix = keyPrefix;
  }

  /// <summary>
  /// The prefix of a logical index in a shared tree. Empty for access to an entire store.
  /// Seek to this prefix and stop when keys no longer start with it.
  /// </summary>
  public ReadOnlyMemory<byte> KeyPrefix { get; }

  /// <summary>The absolute directory of this data tier.</summary>
  public string Directory => store.RootDirectory;

  /// <summary>Returns a snapshot of existing partition names in time order without opening them.</summary>
  public IReadOnlyList<string> GetPartitions()
  {
    ensureOpen();
    return store.ListPartitions();
  }

  /// <summary>
  /// Opens an existing partition and pins its tree and maintainer until the lease is disposed.
  /// Returns null when the partition does not exist or retention has removed it from routing.
  /// </summary>
  /// <remarks>
  /// Opening a closed partition can perform disk I/O. A lease prevents idle closure and
  /// defers partition deletion, but does not stop record expiration, ingestion or compaction.
  /// Release all leases before disposing the engine; engine disposal invalidates every lease.
  /// </remarks>
  public AnalyticsStorageLease<TKey, TValue> Acquire(string partitionName, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
    ensureOpen();
    cancellationToken.ThrowIfCancellationRequested();
    var lease = store.Acquire(partitionName);
    return lease == null ? null : new AnalyticsStorageLease<TKey, TValue>(lease, ensureOpen);
  }

  /// <summary>Asynchronously opens a lease on an existing partition.</summary>
  /// <remarks>Cancellation is checked before opening; an opening already in progress completes.</remarks>
  public Task<AnalyticsStorageLease<TKey, TValue>> AcquireAsync(
      string partitionName, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(partitionName);
    ensureOpen();
    return Task.Run(() => Acquire(partitionName, cancellationToken), cancellationToken);
  }
}

/// <summary>
/// A lease on an analytics partition's actual ZoneTree and existing maintainer.
/// Dispose the lease to release the partition, leaving its resources owned by the engine.
/// </summary>
/// <typeparam name="TKey">The stored key type.</typeparam>
/// <typeparam name="TValue">The stored value type.</typeparam>
public sealed class AnalyticsStorageLease<TKey, TValue> : IDisposable
{
  readonly PartitionLease<TKey, TValue> lease;

  readonly Action ensureOpen;

  int disposed;

  internal AnalyticsStorageLease(PartitionLease<TKey, TValue> lease, Action ensureOpen)
  {
    this.lease = lease;
    this.ensureOpen = ensureOpen;
  }

  /// <summary>The leased partition's directory name.</summary>
  public string PartitionName => lease.Name;

  /// <summary>The actual ZoneTree; valid until the lease or engine is disposed.</summary>
  public IZoneTree<TKey, TValue> ZoneTree
  {
    get
    {
      CheckAccess();
      return lease.Tree;
    }
  }

  /// <summary>The engine's existing maintainer; valid until the lease or engine is disposed.</summary>
  public IMaintainer Maintainer
  {
    get
    {
      CheckAccess();
      return lease.Maintainer;
    }
  }

  void CheckAccess()
  {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    ensureOpen();
  }

  /// <summary>Releases the partition without disposing its ZoneTree or maintainer.</summary>
  public void Dispose()
  {
    if (Interlocked.Exchange(ref disposed, 1) == 0)
      lease.Dispose();
  }
}
