using System.Text.Json;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Engine metadata: storage format settings, the visitor secret, daily salts, rollup activation
/// times and the raw event sequence. A few kilobytes of string pairs, changed rarely.
/// </summary>
/// <remarks>
/// The entries are kept in memory and every change persists all of them, as an indented JSON
/// object with sorted keys in <see cref="AlternatingFiles"/>.
/// </remarks>
sealed class MetaStore
{
  readonly object sync = new();

  readonly AlternatingFiles files;

  Dictionary<string, string> entries;

  public MetaStore(string directory)
  {
    Directory.CreateDirectory(directory);
    files = new(Path.Combine(directory, "meta-a.bin"), Path.Combine(directory, "meta-b.bin"), "ZTAMETA1"u8);
    entries = files.Load(Read);
    // Files exist but none is readable: refuse to start over, which would replace the visitor
    // secret and salts and reset the storage format settings of a directory that holds data.
    if (entries == null && files.Exist)
      throw new AnalyticsConfigurationException(
          $"The analytics metadata in '{directory}' is damaged: no metadata file is valid.");
    entries ??= new(StringComparer.Ordinal);
  }

  public string Get(string key)
  {
    lock (sync)
      return entries.GetValueOrDefault(key);
  }

  /// <summary>
  /// The entries whose key starts with the prefix, ordered by key.
  /// </summary>
  public List<KeyValuePair<string, string>> ListByPrefix(string prefix)
  {
    lock (sync)
    {
      return entries
          .Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal))
          .OrderBy(x => x.Key, StringComparer.Ordinal)
          .ToList();
    }
  }

  public void Set(string key, string value) => Update(x => x[key] = value);

  public void Delete(string key) => Update(x => x.Remove(key));

  /// <summary>
  /// Applies several changes as one atomic write. Nothing is written when nothing changed.
  /// </summary>
  /// <remarks>
  /// A removed entry is written out of both files, not only the newer one: destroyed salts and
  /// secrets must not survive in the older file.
  /// </remarks>
  public void Update(Action<Dictionary<string, string>> change)
  {
    lock (sync)
    {
      var next = new Dictionary<string, string>(entries, StringComparer.Ordinal);
      change(next);
      var removed = entries.Keys.Any(x => !next.ContainsKey(x));
      if (!removed && next.Count == entries.Count &&
          next.All(x => entries.TryGetValue(x.Key, out var value) && value == x.Value))
        return;
      files.Save(stream => Write(stream, next));
      entries = next;
      if (removed)
        files.Save(stream => Write(stream, next));
    }
  }

  static Dictionary<string, string> Read(Stream stream)
  {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    using var document = JsonDocument.Parse(stream);
    foreach (var property in document.RootElement.EnumerateObject())
      result[property.Name] = property.Value.GetString();
    return result;
  }

  static void Write(Stream stream, Dictionary<string, string> entries)
  {
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    foreach (var (key, value) in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
      writer.WriteString(key, value);
    writer.WriteEndObject();
  }
}
