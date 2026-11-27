using ZoneTree.Collections;
using ZoneTree.Comparers;
using ZoneTree.Logging;
using ZoneTree.Serializers;
using ZoneTree.Threading;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>A real tree with a failure injected before or after a selected upsert.</summary>
sealed class FaultingZoneTree<TKey, TValue>(IZoneTree<TKey, TValue> inner) : IZoneTree<TKey, TValue>
{
  int upserts;

  public int FailOnUpsert { get; set; }

  public bool FailAfterWrite { get; set; }

  public Action BeforeDispose { get; set; }

  public int DisposeAttempts { get; private set; }

  public bool Released { get; private set; }

  public long Upsert(in TKey key, in TValue value)
  {
    var fail = ++upserts == FailOnUpsert;
    if (fail && !FailAfterWrite)
      throw new IOException("Injected upsert failure");
    var operation = inner.Upsert(key, value);
    if (fail)
      throw new IOException("Injected failure after updating memory");
    return operation;
  }

  public IRefComparer<TKey> Comparer => inner.Comparer;
  public ISerializer<TKey> KeySerializer => inner.KeySerializer;
  public ISerializer<TValue> ValueSerializer => inner.ValueSerializer;
  public IZoneTreeMaintenance<TKey, TValue> Maintenance => inner.Maintenance;
  public IZoneTreeLogger Logger => inner.Logger;
  public bool IsReadOnly { get => inner.IsReadOnly; set => inner.IsReadOnly = value; }
  public bool ContainsKey(in TKey key) => inner.ContainsKey(key);
  public bool TryGet(in TKey key, out TValue value) => inner.TryGet(key, out value);
  public void VisitSegments<TVisitor>(in TKey key, ref TVisitor visitor)
      where TVisitor : struct, ISegmentRecordVisitor<TKey, TValue> => inner.VisitSegments(key, ref visitor);
  public bool TryAdd(in TKey key, in TValue value, out long opIndex) => inner.TryAdd(key, value, out opIndex);
  public bool TryGetAndUpdate(in TKey key, out TValue value, ValueUpdaterDelegate<TValue> updater, out long opIndex) =>
      inner.TryGetAndUpdate(key, out value, updater, out opIndex);
  public HybridLockScope Lock(in TKey key) => inner.Lock(key);
  public HybridLockScope LockMany(params ReadOnlySpan<TKey> keys) => inner.LockMany(keys);
  public HybridLockScope LockMany(params TKey[] keys) => inner.LockMany(keys);
  public bool TryLock(in TKey key, TimeSpan timeout, out HybridLockScope scope) => inner.TryLock(key, timeout, out scope);
  public ValueTask<HybridLockScope> LockAsync(TKey key, CancellationToken cancellationToken = default) =>
      inner.LockAsync(key, cancellationToken);
  public ValueTask<HybridLockScope> LockManyAsync(ReadOnlySpan<TKey> keys, CancellationToken cancellationToken = default) =>
      inner.LockManyAsync(keys, cancellationToken);
  public ValueTask<HybridLockScope> LockManyAsync(TKey[] keys, CancellationToken cancellationToken = default) =>
      inner.LockManyAsync(keys, cancellationToken);
  public ValueTask<HybridLockScope> LockManyAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default) =>
      inner.LockManyAsync(keys, cancellationToken);
  public long Upsert(in TKey key, GetValueDelegate<TKey, TValue> getter) => inner.Upsert(key, getter);
  public bool TryDelete(in TKey key, out long opIndex) => inner.TryDelete(key, out opIndex);
  public long ForceDelete(in TKey key) => inner.ForceDelete(key);
  public Task SyncWalAsync(CancellationToken cancellationToken = default) => inner.SyncWalAsync(cancellationToken);
  public long Count() => inner.Count();
  public long CountFullScan() => inner.CountFullScan();
  public IMaintainer CreateMaintainer() => inner.CreateMaintainer();
  public IZoneTreeIterator<TKey, TValue> CreateIterator(IteratorOptions options) => inner.CreateIterator(options);
  public IZoneTreeIterator<TKey, TValue> CreateIterator(IteratorType type = IteratorType.AutoRefresh,
      bool includeDeletedRecords = false, bool contributeToTheBlockCache = true) =>
      inner.CreateIterator(type, includeDeletedRecords, contributeToTheBlockCache);
  public IZoneTreeIterator<TKey, TValue> CreateIterator(IteratorType type, bool includeDeletedRecords,
      bool contributeToTheBlockCache, int diskSegmentPrefetchSize) =>
      inner.CreateIterator(type, includeDeletedRecords, contributeToTheBlockCache, diskSegmentPrefetchSize);
  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(IteratorOptions options) => inner.CreateReverseIterator(options);
  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(IteratorType type = IteratorType.AutoRefresh,
      bool includeDeletedRecords = false, bool contributeToTheBlockCache = true) =>
      inner.CreateReverseIterator(type, includeDeletedRecords, contributeToTheBlockCache);
  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(IteratorType type, bool includeDeletedRecords,
      bool contributeToTheBlockCache, int diskSegmentPrefetchSize) =>
      inner.CreateReverseIterator(type, includeDeletedRecords, contributeToTheBlockCache, diskSegmentPrefetchSize);
  public void Dispose()
  {
    ++DisposeAttempts;
    BeforeDispose?.Invoke();
    inner.Dispose();
    Released = true;
  }
}
