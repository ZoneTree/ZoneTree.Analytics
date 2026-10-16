using System.Collections.Concurrent;
using System.Globalization;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Durable write intents, recorded once per affected UTC day before either tree is changed.
/// The intent remains until an orderly shutdown, or until a repair of the day completes and no
/// write of the running engine has recorded it. Recovery replaces absolute copies only in
/// those days, including deletions and out-of-order events.
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

  /// <summary>
  /// Removes the intents of repaired days, except the days this run has written to since it
  /// started: their writes may still need a repair after a crash.
  /// </summary>
  public void ClearRepaired(IEnumerable<long> days)
  {
    lock (sync)
      meta.Update(entries =>
      {
        foreach (var day in days.Where(day => !tracked.ContainsKey(day)))
          entries.Remove(prefix + day.ToString(CultureInfo.InvariantCulture));
      });
  }
}

/// <summary>Seeks past untouched time ranges rather than scanning old event history.</summary>
static class IndexRecoveryScan
{
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

  /// <summary>
  /// Visits the records after <paramref name="after"/> in the given days, or all records when
  /// <paramref name="days"/> is null. Stops after <paramref name="limit"/> records and returns
  /// the last one visited, to continue from; returns an empty key when the tree has no more.
  /// </summary>
  public static Memory<byte> Visit<TValue>(IZoneTree<Memory<byte>, TValue> tree, long[] days,
      KeyHeadLength headLength, Action<Memory<byte>, TValue> visit, Memory<byte> after = default, int limit = int.MaxValue)
  {
    using var iterator = tree.CreateIterator(new IteratorOptions
    {
      IteratorType = IteratorType.NoRefresh, DiskSegmentPrefetchSize = 32, ContributeToTheBlockCache = true
    });
    if (after.IsEmpty) iterator.SeekFirst(); else iterator.Seek(after);
    var visited = 0;
    while (iterator.Next())
    {
      var key = iterator.CurrentKey;
      if (!after.IsEmpty && key.Span.SequenceCompareTo(after.Span) <= 0) continue;
      if (days != null)
      {
        var head = key.Span[..headLength(key.Span)];
        var ticks = KeyCodec.ReadBucket(key.Span, head.Length);
        var day = ticks / TimeSpan.TicksPerDay * TimeSpan.TicksPerDay;
        var index = Array.BinarySearch(days, day);
        if (index < 0)
        {
          index = ~index;
          iterator.Seek(index < days.Length
              ? KeyCodec.SeekKey(head, days[index]) : KeyCodec.AfterPrefix(head));
          continue;
        }
      }
      visit(key, iterator.CurrentValue);
      if (++visited == limit) return key.ToArray();
    }
    return default;
  }
}

/// <summary>
/// Copy operations are idempotent, and a write is durable once it returns: a cursor advances
/// after its copies are written, and resuming repeats at most the copies after it.
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
