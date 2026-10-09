using ZoneTree.Collections.BTree.Lock;
using ZoneTree.Options;

namespace ZoneTree.Analytics;

/// <summary>
/// ZoneTree settings of one kind of store: raw events, rollups or sessions. They apply to
/// every ZoneTree of that kind, including every time partition.
/// </summary>
/// <remarks>
/// <para>
/// Settings left null keep ZoneTree's defaults. Comparers, serializers, deletion markers and key
/// hashers are part of the storage format and are always chosen by the engine.
/// </para>
/// <para>
/// The current partition of every store is opened when the engine starts, so ZoneTree validates
/// these settings before any event is accepted.
/// </para>
/// </remarks>
public sealed class StoreOptions
{
  /// <summary>
  /// The write-ahead-log mode. Default is <see cref="WriteAheadLogMode.AsyncCompressed"/>.
  /// </summary>
  public WriteAheadLogMode WriteAheadLogMode { get; set; } = WriteAheadLogMode.AsyncCompressed;

  /// <summary>
  /// How many recent records are held in the mutable (in-memory) segment before it moves
  /// toward disk.
  /// </summary>
  public int? MutableSegmentMaxItemCount { get; set; }

  /// <summary>
  /// Bloom filter bits per mutable segment record, 0 to 64. Zero disables the filter, which
  /// suits stores that are only scanned and never point-read.
  /// </summary>
  public int? MutableSegmentBloomFilterBitsPerItem { get; set; }

  /// <summary>
  /// How many records a disk segment holds before merges start a new one.
  /// </summary>
  public int? DiskSegmentMaxItemCount { get; set; }

  /// <summary>
  /// The lock mode of the in-memory B+tree.
  /// </summary>
  public BTreeLockMode? BTreeLockMode { get; set; }

  /// <summary>
  /// The node size of the in-memory B+tree.
  /// </summary>
  public int? BTreeNodeSize { get; set; }

  /// <summary>
  /// The leaf size of the in-memory B+tree.
  /// </summary>
  public int? BTreeLeafSize { get; set; }

  /// <summary>
  /// Removes deleted records on load when a tree consists of its mutable segment only.
  /// </summary>
  public bool? EnableSingleSegmentGarbageCollection { get; set; }

  /// <summary>
  /// Lets ZoneTree accept option values outside its recommended ranges.
  /// </summary>
  public bool AllowUnsafeOptionValues { get; set; }

  /// <summary>
  /// Tunes the write-ahead log, for example compression or incremental backup. Runs after
  /// <see cref="WriteAheadLogMode"/> is applied.
  /// </summary>
  public Action<WriteAheadLogOptions> ConfigureWriteAheadLog { get; set; }

  /// <summary>
  /// Tunes disk segments, for example the segment mode, compression or caches.
  /// </summary>
  public Action<DiskSegmentOptions> ConfigureDiskSegments { get; set; }

  /// <summary>
  /// Tunes the maintainer of every tree, for example merge thresholds or block cache cleanup.
  /// </summary>
  public Action<IMaintainer> ConfigureMaintainer { get; set; }

  internal StoreOptions Clone() => (StoreOptions)MemberwiseClone();

  internal void Validate(string store)
  {
    if (!Enum.IsDefined(WriteAheadLogMode))
      throw new ArgumentException($"Storage.{store}.WriteAheadLogMode is not a valid value.");
    if (BTreeLockMode is { } lockMode && !Enum.IsDefined(lockMode))
      throw new ArgumentException($"Storage.{store}.BTreeLockMode is not a valid value.");
    RequirePositive(MutableSegmentMaxItemCount, nameof(MutableSegmentMaxItemCount));
    RequirePositive(DiskSegmentMaxItemCount, nameof(DiskSegmentMaxItemCount));
    RequirePositive(BTreeNodeSize, nameof(BTreeNodeSize));
    RequirePositive(BTreeLeafSize, nameof(BTreeLeafSize));
    if (MutableSegmentBloomFilterBitsPerItem is < 0 or > 64)
      throw new ArgumentException($"Storage.{store}.MutableSegmentBloomFilterBitsPerItem must be between 0 and 64.");

    void RequirePositive(int? value, string name)
    {
      if (value < 1)
        throw new ArgumentException($"Storage.{store}.{name} must be positive.");
    }
  }
}
