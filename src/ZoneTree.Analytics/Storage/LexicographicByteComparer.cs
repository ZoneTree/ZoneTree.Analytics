using ZoneTree.Comparers;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Orders byte keys lexicographically: by content, and a key sorts before every
/// longer key it is a prefix of.
/// </summary>
/// <remarks>
/// Prefix ordering is what makes prefix seeks work: seeking to a series prefix or to
/// prefix + bucket positions the iterator on the first matching row. ZoneTree's
/// built-in <see cref="ByteArrayComparerAscending"/> breaks ties on length the other way
/// (longer keys first), so it is not used here. Equality is plain content equality, so
/// ZoneTree's built-in byte-array key hasher remains compatible.
/// The type name is part of the persisted ZoneTree metadata and must stay stable.
/// </remarks>
sealed class LexicographicByteComparer : IRefComparer<Memory<byte>>
{
  public int Compare(in Memory<byte> x, in Memory<byte> y) =>
      x.Span.SequenceCompareTo(y.Span);
}
