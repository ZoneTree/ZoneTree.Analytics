using System.Collections.Concurrent;

namespace ZoneTree.Analytics.Web;

/// <summary>
/// What is derived from user agents, for up to 10,000 distinct ones: a full cache starts over.
/// User agents are cut to their first 1,024 characters first, which bounds the work and memory of
/// unusually long ones.
/// </summary>
sealed class UserAgentCache<T>(Func<string, T> derive)
{
  const int MaxCount = 10_000;

  const int MaxLength = 1024;

  readonly ConcurrentDictionary<string, T> cache = new(StringComparer.Ordinal);

  /// <summary>What is derived from a user agent; the default value for a missing or blank one.</summary>
  public T Get(string userAgent)
  {
    if (string.IsNullOrWhiteSpace(userAgent))
      return default;
    if (userAgent.Length > MaxLength)
      userAgent = userAgent[..MaxLength];
    if (cache.TryGetValue(userAgent, out var cached))
      return cached;
    var derived = derive(userAgent);
    if (cache.Count >= MaxCount)
      cache.Clear();
    cache.TryAdd(userAgent, derived);
    return derived;
  }
}
