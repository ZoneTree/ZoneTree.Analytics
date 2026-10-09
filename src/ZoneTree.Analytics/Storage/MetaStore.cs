using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Engine metadata: storage format settings, the visitor secret, daily salts, rollup activation
/// times and the raw event sequence. A few kilobytes of string pairs, changed rarely.
/// </summary>
/// <remarks>
/// <para>
/// The entries are kept in memory and every change persists all of them. Two files are written
/// alternately, each in place and flushed to disk, and each records a generation number and a
/// checksum. A write always replaces the older file, so a crash can only damage the file being
/// written; loading picks the valid file with the highest generation. No rename and no directory
/// flush are involved, which makes the scheme equally durable on every platform.
/// </para>
/// <para>
/// File layout: a header line <c>zonetree-analytics-meta {generation} {sha256 of the rest}</c>
/// followed by the entries as an indented JSON object with sorted keys.
/// </para>
/// </remarks>
sealed class MetaStore
{
  const string Header = "zonetree-analytics-meta";

  readonly object sync = new();

  readonly string[] paths;

  Dictionary<string, string> entries;

  long generation;

  public MetaStore(string directory)
  {
    Directory.CreateDirectory(directory);
    paths = [Path.Combine(directory, "meta-a.json"), Path.Combine(directory, "meta-b.json")];
    (entries, generation) = Load();
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
      WriteNextGeneration(next);
      entries = next;
      if (removed)
        WriteNextGeneration(next);
    }
  }

  void WriteNextGeneration(Dictionary<string, string> content)
  {
    Write(paths[(generation + 1) % 2], generation + 1, content);
    ++generation;
  }

  (Dictionary<string, string> Entries, long Generation) Load()
  {
    var found = false;
    (Dictionary<string, string> Entries, long Generation) newest = (new(StringComparer.Ordinal), 0);
    foreach (var path in paths)
    {
      if (!File.Exists(path))
        continue;
      found = true;
      if (TryRead(path, out var candidate) && candidate.Generation > newest.Generation)
        newest = candidate;
    }
    // Files exist but none is readable: refuse to start over, which would replace the visitor
    // secret and salts and reset the storage format settings of a directory that holds data.
    if (found && newest.Generation == 0)
      throw new AnalyticsConfigurationException(
          $"The analytics metadata in '{Path.GetDirectoryName(paths[0])}' is damaged: no metadata file is valid.");
    return newest;
  }

  static bool TryRead(string path, out (Dictionary<string, string> Entries, long Generation) result)
  {
    result = default;
    var bytes = File.ReadAllBytes(path);
    var newline = Array.IndexOf(bytes, (byte)'\n');
    if (newline < 0)
      return false;
    var header = Encoding.UTF8.GetString(bytes, 0, newline).Split(' ');
    var body = bytes.AsSpan(newline + 1);
    if (header.Length != 3 || header[0] != Header ||
        !long.TryParse(header[1], NumberStyles.None, CultureInfo.InvariantCulture, out var fileGeneration) ||
        fileGeneration < 1 ||
        !string.Equals(header[2], Checksum(body), StringComparison.Ordinal))
      return false;
    var entries = new Dictionary<string, string>(StringComparer.Ordinal);
    using (var document = JsonDocument.Parse(body.ToArray()))
    {
      foreach (var property in document.RootElement.EnumerateObject())
        entries[property.Name] = property.Value.GetString();
    }
    result = (entries, fileGeneration);
    return true;
  }

  static void Write(string path, long fileGeneration, Dictionary<string, string> entries)
  {
    var body = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(body, new JsonWriterOptions { Indented = true }))
    {
      writer.WriteStartObject();
      foreach (var (key, value) in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
        writer.WriteString(key, value);
      writer.WriteEndObject();
    }
    var header = Encoding.UTF8.GetBytes(string.Create(
        CultureInfo.InvariantCulture, $"{Header} {fileGeneration} {Checksum(body.WrittenSpan)}\n"));
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    stream.Write(header);
    stream.Write(body.WrittenSpan);
    stream.Flush(flushToDisk: true);
  }

  static string Checksum(ReadOnlySpan<byte> body) => Convert.ToHexString(SHA256.HashData(body));
}
