using ZoneTree.Collections.BTree;
using ZoneTree.Comparers;
using ZoneTree.Logger;
using ZoneTree.Serializers;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>A real tree with a failure injected before or after a selected upsert.</summary>
sealed class FaultingZoneTree<TKey, TValue>(IZoneTree<TKey, TValue> inner) : IZoneTree<TKey, TValue>
{
  int upserts;

  public int FailOnUpsert { get; set; }

  public bool FailAfterWrite { get; set; }

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
  public ILogger Logger => inner.Logger;
  public bool IsReadOnly { get => inner.IsReadOnly; set => inner.IsReadOnly = value; }
  public bool ContainsKey(in TKey key) => inner.ContainsKey(key);
  public bool TryGet(in TKey key, out TValue value) => inner.TryGet(key, out value);
  public bool TryAdd(in TKey key, in TValue value, out long opIndex) => inner.TryAdd(key, value, out opIndex);
  public bool TryGetAndUpdate(in TKey key, out TValue value, ValueUpdaterDelegate<TValue> updater, out long opIndex) =>
      inner.TryGetAndUpdate(key, out value, updater, out opIndex);
  public bool TryAtomicGetAndUpdate(in TKey key, out TValue value, ValueUpdaterDelegate<TValue> updater) =>
      inner.TryAtomicGetAndUpdate(key, out value, updater);
  public bool TryAtomicAdd(in TKey key, in TValue value, out long opIndex) => inner.TryAtomicAdd(key, value, out opIndex);
  public bool TryAtomicUpdate(in TKey key, in TValue value, out long opIndex) => inner.TryAtomicUpdate(key, value, out opIndex);
  public bool TryAtomicAddOrUpdate(in TKey key, in TValue value, ValueUpdaterDelegate<TValue> updater,
      OperationResultDelegate<TValue> result = null) => inner.TryAtomicAddOrUpdate(key, value, updater, result);
  public bool TryAtomicAddOrUpdate(in TKey key, ValueAdderDelegate<TValue> adder, ValueUpdaterDelegate<TValue> updater,
      OperationResultDelegate<TValue> result = null) => inner.TryAtomicAddOrUpdate(key, adder, updater, result);
  public long AtomicUpsert(in TKey key, in TValue value) => inner.AtomicUpsert(key, value);
  public long Upsert(in TKey key, GetValueDelegate<TKey, TValue> getter) => inner.Upsert(key, getter);
  public bool TryDelete(in TKey key, out long opIndex) => inner.TryDelete(key, out opIndex);
  public long ForceDelete(in TKey key) => inner.ForceDelete(key);
  public long Count() => inner.Count();
  public long CountFullScan() => inner.CountFullScan();
  public IMaintainer CreateMaintainer() => inner.CreateMaintainer();
  public IZoneTreeIterator<TKey, TValue> CreateIterator(IteratorOptions options) => inner.CreateIterator(options);
  public IZoneTreeIterator<TKey, TValue> CreateIterator(IteratorType type = IteratorType.AutoRefresh,
      bool includeDeletedRecords = false, bool contributeToTheBlockCache = false) =>
      inner.CreateIterator(type, includeDeletedRecords, contributeToTheBlockCache);
  public IZoneTreeIterator<TKey, TValue> CreateIterator(IteratorType type, bool includeDeletedRecords,
      bool contributeToTheBlockCache, int diskSegmentPrefetchSize) =>
      inner.CreateIterator(type, includeDeletedRecords, contributeToTheBlockCache, diskSegmentPrefetchSize);
  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(IteratorOptions options) => inner.CreateReverseIterator(options);
  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(IteratorType type = IteratorType.AutoRefresh,
      bool includeDeletedRecords = false, bool contributeToTheBlockCache = false) =>
      inner.CreateReverseIterator(type, includeDeletedRecords, contributeToTheBlockCache);
  public IZoneTreeIterator<TKey, TValue> CreateReverseIterator(IteratorType type, bool includeDeletedRecords,
      bool contributeToTheBlockCache, int diskSegmentPrefetchSize) =>
      inner.CreateReverseIterator(type, includeDeletedRecords, contributeToTheBlockCache, diskSegmentPrefetchSize);
  public void Dispose() => inner.Dispose();
}
