using System.Collections;

namespace ZoneTree.Analytics.Ingestion;

/// <summary>
/// A session's session-level dimensions and entry page, read in place from its state.
/// </summary>
/// <remarks>
/// A worker reuses one instance for every session row, so building a row allocates nothing.
/// The view is valid until it is pointed at another session.
/// </remarks>
sealed class SessionDimensions : IReadOnlyDictionary<string, string>
{
  readonly SessionSchema schema;

  Storage.SessionState session;

  public SessionDimensions(SessionSchema schema)
  {
    this.schema = schema;
  }

  public SessionDimensions Of(Storage.SessionState state)
  {
    session = state;
    return this;
  }

  public bool TryGetValue(string key, out string value)
  {
    if (key == StandardDimensions.EntryPage)
    {
      value = session.EntryPage;
      return true;
    }
    var index = schema.IndexOf(key);
    value = index >= 0 && index < session.Dimensions.Length ? session.Dimensions[index] : null;
    return value != null;
  }

  public string this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

  public bool ContainsKey(string key) => TryGetValue(key, out _);

  public int Count => session.Dimensions.Count(x => x != null) + 1;

  public IEnumerable<string> Keys => this.Select(x => x.Key);

  public IEnumerable<string> Values => this.Select(x => x.Value);

  public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
  {
    for (var i = 0; i < session.Dimensions.Length; ++i)
    {
      if (session.Dimensions[i] != null)
        yield return new(schema.Names[i], session.Dimensions[i]);
    }
    yield return new(StandardDimensions.EntryPage, session.EntryPage);
  }

  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Session dimensions with a page and hostname laid over them, for entrance and exit rows.
/// </summary>
/// <remarks>
/// A worker reuses one instance for every row, so building a row neither copies the session
/// dimensions nor allocates. The view is valid until it is pointed at another page.
/// </remarks>
sealed class PageDimensions : IReadOnlyDictionary<string, string>
{
  IReadOnlyDictionary<string, string> session;

  string page;

  string hostname;

  public PageDimensions Of(IReadOnlyDictionary<string, string> sessionDimensions, string page, string hostname)
  {
    session = sessionDimensions;
    this.page = page;
    this.hostname = hostname ?? "";
    return this;
  }

  public bool TryGetValue(string key, out string value)
  {
    switch (key)
    {
      case StandardDimensions.Page:
        value = page;
        return true;
      case StandardDimensions.Hostname:
        value = hostname;
        return true;
      default:
        return session.TryGetValue(key, out value);
    }
  }

  public string this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

  public bool ContainsKey(string key) => TryGetValue(key, out _);

  public int Count => session.Count + 2;

  public IEnumerable<string> Keys => this.Select(x => x.Key);

  public IEnumerable<string> Values => this.Select(x => x.Value);

  public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
  {
    // Session dimensions never include page or hostname: those are page-view dimensions.
    foreach (var entry in session)
      yield return entry;
    yield return new(StandardDimensions.Page, page);
    yield return new(StandardDimensions.Hostname, hostname);
  }

  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
