namespace ZoneTree.Analytics.Internal;

/// <summary>
/// Thread-safe lazy fields without locks: concurrent first reads may each create a value, and
/// all of them get the first one stored.
/// </summary>
static class Once
{
  /// <summary>The value of a field, stored from <paramref name="create"/> when the field is null.</summary>
  public static T Get<T, TState>(ref T field, TState state, Func<TState, T> create) where T : class
  {
    var current = Volatile.Read(ref field);
    if (current != null)
      return current;
    var created = create(state);
    return Interlocked.CompareExchange(ref field, created, null) ?? created;
  }
}
