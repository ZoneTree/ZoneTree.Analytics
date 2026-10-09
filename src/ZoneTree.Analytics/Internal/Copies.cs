namespace ZoneTree.Analytics.Internal;

/// <summary>
/// Copies of option collections that keep their comparers.
/// </summary>
static class Copies
{
  public static ISet<string> Of(ISet<string> set) => set switch
  {
    null => null,
    HashSet<string> hashSet => new HashSet<string>(hashSet, hashSet.Comparer),
    _ => new HashSet<string>(set, StringComparer.Ordinal)
  };

  public static IDictionary<string, string> Of(IDictionary<string, string> dictionary) => dictionary switch
  {
    null => null,
    Dictionary<string, string> concrete => new Dictionary<string, string>(concrete, concrete.Comparer),
    _ => new Dictionary<string, string>(dictionary, StringComparer.Ordinal)
  };
}
