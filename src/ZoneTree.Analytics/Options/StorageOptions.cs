using ZoneTree.Logger;

namespace ZoneTree.Analytics;

/// <summary>
/// Storage options.
/// </summary>
public sealed class StorageOptions
{
  /// <summary>
  /// Stores every enriched event. Raw events answer ad-hoc queries over any dimension
  /// combination, real-time visitor counts and visitor erasure requests.
  /// When disabled, only rollups are kept. Default is true.
  /// </summary>
  public bool StoreRawEvents { get; set; } = true;

  /// <summary>
  /// How many records each ZoneTree partition of a data tier holds before the tier starts a new
  /// one. It can be changed at any time; it applies to the partitions started afterwards.
  /// </summary>
  public PartitioningOptions Partitioning { get; private set; } = new();

  /// <summary>
  /// ZoneTree settings of raw event partitions. Raw events are only scanned, never point-read,
  /// so by default the mutable segment holds 50,000 events and has no Bloom filter.
  /// </summary>
  public StoreOptions RawEvents { get; private set; } = new()
  {
    MutableSegmentMaxItemCount = 50_000,
    ScannedOnly = true
  };

  /// <summary>Opt-in full-payload ordered dimension indexes over retained raw events.</summary>
  public DimensionIndexOptions DimensionIndexes { get; private set; } = new();

  /// <summary>Opt-in full-key, full-value ordered indexes of joint rollups.</summary>
  public RollupIndexOptions RollupIndexes { get; private set; } = new();

  /// <summary>
  /// ZoneTree settings of rollup partitions. By default the mutable segment holds 100,000 rows.
  /// </summary>
  /// <remarks>
  /// Rollup keys are ordered by series, then time, so the rows of a new hour spread over the whole
  /// key range of a tree, and merges rewrite most of its disk segment. A larger mutable segment
  /// merges less often and absorbs repeated updates of the same row; a smaller one holds less
  /// memory, as rows carry visitor sketches. Raising it from 20,000 to 100,000 rows lowered the
  /// ingestion CPU time by about 7%, and to 200,000 by about 11%.
  /// </remarks>
  public StoreOptions Rollups { get; private set; } = new() { MutableSegmentMaxItemCount = 100_000 };

  /// <summary>
  /// ZoneTree settings of the active session store. By default the mutable segment holds
  /// 50,000 sessions.
  /// </summary>
  public StoreOptions Sessions { get; private set; } = new() { MutableSegmentMaxItemCount = 50_000 };

  /// <summary>
  /// A separate root directory for write-ahead logs, for example on a faster disk. Each store
  /// keeps its log under the same relative path as its data. Null (the default) keeps logs next
  /// to the data. It is persisted when the data directory is created and cannot be changed
  /// afterwards, because unflushed records live in the log.
  /// </summary>
  public string WriteAheadLogDirectory { get; set; }

  /// <summary>
  /// Partitions that were not used for this long are closed. Default is 10 minutes.
  /// </summary>
  public TimeSpan PartitionIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

  /// <summary>
  /// The logger used by the engine and its ZoneTree stores.
  /// Default is a console logger at warning level.
  /// </summary>
  public ILogger Logger { get; set; }

  internal StorageOptions Clone()
  {
    var clone = (StorageOptions)MemberwiseClone();
    clone.Partitioning = Partitioning.Clone();
    clone.RawEvents = RawEvents.Clone();
    clone.DimensionIndexes = DimensionIndexes.Clone();
    clone.RollupIndexes = RollupIndexes.Clone();
    clone.Rollups = Rollups.Clone();
    clone.Sessions = Sessions.Clone();
    return clone;
  }

  internal void Validate()
  {
    if (PartitionIdleTimeout <= TimeSpan.Zero)
      throw new ArgumentException("Storage.PartitionIdleTimeout must be positive.");
    if (WriteAheadLogDirectory != null && string.IsNullOrWhiteSpace(WriteAheadLogDirectory))
      throw new ArgumentException("Storage.WriteAheadLogDirectory cannot be empty; use null to keep logs next to the data.");
    RawEvents.Validate(nameof(RawEvents));
    DimensionIndexes.Validate(StoreRawEvents);
    RollupIndexes.Validate();
    Rollups.Validate(nameof(Rollups));
    Sessions.Validate(nameof(Sessions));
    Partitioning.Validate();
  }
}

/// <summary>
/// The size of the ZoneTree partitions of each data tier, in records.
/// </summary>
/// <remarks>
/// <para>
/// Each data tier writes to its latest partition until that partition holds the configured
/// number of records; then the tier starts a new partition at a later day boundary (at least a
/// day ahead, so that no record written so far falls into the new partition's range). A
/// partition holds a contiguous time range, so queries read only the partitions that overlap
/// their range. Records are counted approximately: deleted records that merges have not removed
/// yet are included.
/// </para>
/// <para>
/// A ZoneTree comfortably holds tens of millions of records, so the default is 10 million
/// records per partition for every tier. Fewer, larger partitions mean fewer trees, files and
/// directories, and fewer trees per long-range query. Smaller partitions are only useful for
/// tests.
/// </para>
/// <para>
/// Retention does not depend on partitions: records older than retention are deleted at once
/// (see <see cref="RetentionOptions"/>), removed from disk by merges, and partitions that expire
/// as a whole are dropped. Events with an explicit timestamp go to the partition that holds
/// their time, so a backfill adds to existing partitions.
/// </para>
/// </remarks>
public sealed class PartitioningOptions
{
  /// <summary>
  /// The default number of records per partition: 10 million.
  /// </summary>
  public const long DefaultRecordsPerPartition = 10_000_000;

  /// <summary>
  /// Raw events per partition. Default is <see cref="DefaultRecordsPerPartition"/>.
  /// </summary>
  public long RawEvents { get; set; } = DefaultRecordsPerPartition;

  /// <summary>
  /// Minute rollup rows per partition. Default is <see cref="DefaultRecordsPerPartition"/>.
  /// </summary>
  public long MinuteRollups { get; set; } = DefaultRecordsPerPartition;

  /// <summary>
  /// Hourly rollup rows per partition. Default is <see cref="DefaultRecordsPerPartition"/>.
  /// </summary>
  public long HourRollups { get; set; } = DefaultRecordsPerPartition;

  /// <summary>
  /// Daily rollup rows per partition. Default is <see cref="DefaultRecordsPerPartition"/>.
  /// </summary>
  public long DayRollups { get; set; } = DefaultRecordsPerPartition;

  internal PartitioningOptions Clone() => (PartitioningOptions)MemberwiseClone();

  internal void Validate()
  {
    foreach (var (tier, records) in new[] { (nameof(RawEvents), RawEvents), (nameof(MinuteRollups), MinuteRollups), (nameof(HourRollups), HourRollups), (nameof(DayRollups), DayRollups) })
    {
      if (records <= 0)
        throw new ArgumentException($"Storage.Partitioning.{tier} must be positive.");
    }
  }
}
