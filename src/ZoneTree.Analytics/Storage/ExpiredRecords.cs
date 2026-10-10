namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Tells whether the record with a key has expired.
/// </summary>
delegate bool ExpiredKeyPredicate(ReadOnlySpan<byte> key);

/// <summary>
/// Removes the records that deletion predicates hide because they are older than retention
/// from disk.
/// </summary>
/// <remarks>
/// <para>
/// A ZoneTree merge drops the deleted records it reads. It reads every disk segment part whose
/// first record is deleted, and every part that a newer record falls into; it copies the other
/// parts as they are. Keys are ordered by head (the site of a raw event; the site, event and
/// dimension set of a rollup row), then by time, so the expired records of a head are its first
/// ones.
/// </para>
/// <para>
/// Deleting the first record of every head whose first record has expired makes the next merge
/// read the part that holds it, even when that part starts with the newer records of another
/// head, and drop every expired record from there on: the following parts of the head start
/// with expired records until the part where the cutoff falls, which the merge rewrites without
/// them. Deleting an expired record loses nothing, and a tree that receives no new events gets
/// the record a merge needs to run.
/// </para>
/// <para>
/// Merges skip reading a part altogether when <see cref="IsRangeExpired"/> proves from its
/// first and last keys that every record in it has expired: ZoneTree's range deletion drops
/// the part without decompressing it. That holds for the parts that lie within one head, such
/// as the old events of a busy site; parts that span heads are read and filtered as above.
/// </para>
/// </remarks>
static class ExpiredRecords
{
  /// <summary>
  /// True when every key from <paramref name="first"/> to <paramref name="last"/> has expired:
  /// both keys share a head and the last one has expired. Keys of one head are ordered by time,
  /// so every key between them is older than the last. A range that spans heads holds the end
  /// of its first head, its newest records, so it is never reported expired.
  /// </summary>
  /// <param name="first">The first key of the range.</param>
  /// <param name="last">The last key of the range.</param>
  /// <param name="headSegments">The number of key segments that make up a head.</param>
  /// <param name="isExpired">The retention part of the tree's deletion predicate.</param>
  public static bool IsRangeExpired(
      ReadOnlySpan<byte> first, ReadOnlySpan<byte> last, int headSegments, ExpiredKeyPredicate isExpired)
  {
    var head = KeyCodec.HeadLength(first, headSegments);
    return last.Length > head && last[..head].SequenceEqual(first[..head]) && isExpired(last);
  }

  /// <summary>
  /// Deletes the first record of every head whose first record has expired.
  /// Returns the number of records deleted.
  /// </summary>
  /// <param name="tree">The tree.</param>
  /// <param name="headSegments">The number of key segments that make up a head.</param>
  /// <param name="isExpired">The retention part of the tree's deletion predicate.</param>
  public static int DeleteFirstOfExpiredHeads<TValue>(
      IZoneTree<Memory<byte>, TValue> tree, int headSegments, ExpiredKeyPredicate isExpired)
  {
    var deleted = 0;
    // Deleted records are included: hidden expired records are what it looks for. Each delete
    // lands on the key just read, which the iterator has already passed.
    using var iterator = tree.CreateIterator(new IteratorOptions
    {
      IteratorType = IteratorType.NoRefresh,
      IncludeDeletedRecords = true
    });
    iterator.SeekFirst();
    while (iterator.Next())
    {
      var key = iterator.CurrentKey;
      if (isExpired(key.Span))
      {
        tree.ForceDelete(key);
        ++deleted;
      }
      // Skip to the next head: past every key that starts with this head.
      var head = key.Span[..(KeyCodec.HeadLength(key.Span, headSegments) - 1)];
      iterator.Seek(KeyCodec.AfterSegment(head));
    }
    return deleted;
  }
}
