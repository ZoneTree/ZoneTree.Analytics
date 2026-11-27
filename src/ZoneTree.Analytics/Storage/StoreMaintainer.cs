using ZoneTree.Segments;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// A ZoneTree maintainer that also merges bottom segments.
/// </summary>
/// <remarks>
/// A disk segment that grows past <c>DiskSegmentMaxItemCount</c> becomes a bottom segment.
/// While bottom segments exist, regular merges keep deleted records, so expired records leave
/// the disk when bottom segments merge. Whenever a merge activates a disk segment and two or
/// more bottom segments exist, they are merged, which keeps large trees to few bottom segments
/// and removes their deleted records.
/// </remarks>
sealed class StoreMaintainer<TKey, TValue> : IMaintainer
{
  readonly IMaintainer inner;

  readonly IZoneTreeMaintenance<TKey, TValue> maintenance;

  public StoreMaintainer(IZoneTree<TKey, TValue> tree)
  {
    inner = tree.CreateMaintainer();
    maintenance = tree.Maintenance;
    maintenance.OnDiskSegmentActivated += OnDiskSegmentActivated;
  }

  void OnDiskSegmentActivated(
      IZoneTreeMaintenance<TKey, TValue> tree, IDiskSegment<TKey, TValue> segment, bool isBottomSegment)
  {
    if (tree.IsBottomSegmentsMerging || tree.BottomSegments.Count < 2)
      return;
    inner.StartBottomSegmentsMerge();
  }

  public int MergeThreshold
  {
    get => inner.MergeThreshold;
    set => inner.MergeThreshold = value;
  }

  public int MaximumReadOnlySegmentCount
  {
    get => inner.MaximumReadOnlySegmentCount;
    set => inner.MaximumReadOnlySegmentCount = value;
  }

  public bool EnableInactiveCacheCleanup
  {
    get => inner.EnableInactiveCacheCleanup;
    set => inner.EnableInactiveCacheCleanup = value;
  }

  public TimeSpan BlockCacheLifetime
  {
    get => inner.BlockCacheLifetime;
    set => inner.BlockCacheLifetime = value;
  }

  public TimeSpan InactiveBlockCacheCleanupInterval
  {
    get => inner.InactiveBlockCacheCleanupInterval;
    set => inner.InactiveBlockCacheCleanupInterval = value;
  }

  public void TryCancelBackgroundThreads() => inner.TryCancelBackgroundThreads();

  public void WaitForBackgroundThreads() => inner.WaitForBackgroundThreads();

  public Task WaitForBackgroundThreadsAsync() => inner.WaitForBackgroundThreadsAsync();

  public void EvictToDisk() => inner.EvictToDisk();

  public void StartMerge() => inner.StartMerge();

  public void StartBottomSegmentsMerge(int fromIndex = 0, int toIndex = int.MaxValue) =>
      inner.StartBottomSegmentsMerge(fromIndex, toIndex);

  public void Dispose()
  {
    maintenance.OnDiskSegmentActivated -= OnDiskSegmentActivated;
    inner.Dispose();
  }
}
