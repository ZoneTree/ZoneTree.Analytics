using System.Collections.Concurrent;
using System.Globalization;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Durable write intents, recorded once per affected UTC day before either tree is changed.
/// The intent remains until both trees have drained their WALs. Recovery replaces absolute
/// copies only in those days, including deletions and out-of-order events.
/// </summary>
sealed class IndexRecovery(MetaStore meta, string prefix)
{
  readonly ConcurrentDictionary<long, byte> tracked = new();
  readonly object sync = new();

  public long[] Days => meta.ListByPrefix(prefix).Select(entry =>
      long.Parse(entry.Key[prefix.Length..], CultureInfo.InvariantCulture)).Order().ToArray();

  public void Track(long ticks)
  {
    var day = ticks / TimeSpan.TicksPerDay * TimeSpan.TicksPerDay;
    if (tracked.ContainsKey(day)) return;
    lock (sync)
    {
      if (tracked.ContainsKey(day)) return;
      meta.Set(prefix + day.ToString(CultureInfo.InvariantCulture), "true");
      tracked.TryAdd(day, 0);
    }
  }

  public void Clear(Dictionary<string, string> entries)
  {
    foreach (var key in entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
      entries.Remove(key);
  }
}

/// <summary>Seeks past untouched time ranges rather than scanning old event history.</summary>
static class IndexRecoveryScan
{
  public delegate int HeadLength(ReadOnlySpan<byte> key);

  public static bool MissingRetainedPartitions(string[] expected, IReadOnlyList<string> actual, long cutoff)
  {
    var all = expected.Union(actual).Order(StringComparer.Ordinal).ToArray();
    foreach (var missing in expected.Except(actual))
    {
      var index = Array.IndexOf(all, missing);
      // A missing latest partition has no known upper boundary and must be rebuilt.
      if (index == all.Length - 1 || cutoff == long.MinValue ||
          DateTime.ParseExact(all[index + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture).Ticks > cutoff) return true;
    }
    return false;
  }

  public static void Visit<TValue>(IZoneTree<Memory<byte>, TValue> tree, long[] days,
      HeadLength headLength, Action<Memory<byte>, TValue> visit, Memory<byte> after = default)
  {
    using var iterator = tree.CreateIterator(new IteratorOptions
    {
      IteratorType = IteratorType.NoRefresh, DiskSegmentPrefetchSize = 32, ContributeToTheBlockCache = true
    });
    if (after.IsEmpty) iterator.SeekFirst(); else iterator.Seek(after);
    while (iterator.Next())
    {
      var key = iterator.CurrentKey;
      if (!after.IsEmpty && key.Span.SequenceCompareTo(after.Span) <= 0) continue;
      if (days == null) { visit(key, iterator.CurrentValue); continue; }
      var head = key.Span[..headLength(key.Span)];
      var ticks = KeyCodec.ReadBucket(key.Span, head.Length);
      var day = ticks / TimeSpan.TicksPerDay * TimeSpan.TicksPerDay;
      var index = Array.BinarySearch(days, day);
      if (index >= 0) { visit(key, iterator.CurrentValue); continue; }
      index = ~index;
      iterator.Seek(index < days.Length
          ? KeyCodec.SeekKey(head, days[index]) : DimensionIndexKeyCodec.AfterPrefix(head));
    }
  }

  public static void Persist<TValue>(IEnumerable<PartitionedStore<Memory<byte>, TValue>> stores)
  {
    foreach (var store in stores)
    {
      store.Compact();
      store.CloseIdle(TimeSpan.Zero, keepLatestOpen: false);
    }
  }
}

/// <summary>
/// Copy operations are idempotent. A cursor advances only after destination copies reach
/// disk; replaying the unfinished chunk cannot add events or aggregate deltas twice.
/// </summary>
sealed class IndexBackfillCheckpoint(MetaStore meta, string prefix, string configuration)
{
  public const int Interval = 100_000;
  public bool Resuming => meta.Get(prefix + ".config") == configuration;
  public bool Copying => Resuming && meta.Get(prefix + ".phase") == "copy";
  public string Partition => meta.Get(prefix + ".partition") ?? "";
  public Memory<byte> Key => meta.Get(prefix + ".key") is { Length: > 0 } key ? Convert.FromBase64String(key) : default;
  public long Count => long.TryParse(meta.Get(prefix + ".count"), out var count) ? count : 0;

  public void Begin() => meta.Update(entries =>
  {
    Clear(entries);
    entries[prefix + ".config"] = configuration;
    entries[prefix + ".phase"] = "clear";
  });

  public void Cleared() => meta.Set(prefix + ".phase", "copy");

  public void Save(string partition, Memory<byte> key, long count) => meta.Update(entries =>
  {
    entries[prefix + ".partition"] = partition;
    entries[prefix + ".key"] = Convert.ToBase64String(key.Span);
    entries[prefix + ".count"] = count.ToString(CultureInfo.InvariantCulture);
  });

  public void Clear(Dictionary<string, string> entries)
  {
    foreach (var key in entries.Keys.Where(key => key.StartsWith(prefix + ".", StringComparison.Ordinal)).ToArray())
      entries.Remove(key);
  }
}
