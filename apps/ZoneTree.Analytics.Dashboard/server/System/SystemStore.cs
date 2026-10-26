using System.Text.Json;
using System.Text.Json.Serialization;
using ZoneTree;

// The application's own state lives in this one ZoneTree: users, sessions, settings, secrets
// and the audit log share it under key prefixes. Values are JSON documents.
sealed class SystemStore : IDisposable
{
  public const string DefaultDirectory = "data/system";
  const string Marker = ".zonetree-dashboard-system";

  public static readonly JsonSerializerOptions Json = CreateJsonOptions();

  readonly IZoneTree<string, string> tree;
  readonly IMaintainer maintainer;

  public string Directory { get; }

  // Callers hold it across a read-check-write sequence, for example a unique user name.
  public object WriteLock { get; } = new();

  public SystemStore(string directory)
  {
    Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    EnsureOwnedDirectory();
    tree = new ZoneTreeFactory<string, string>()
        .SetDataDirectory(Directory)
        .OpenOrCreate();
    maintainer = tree.CreateMaintainer();
  }

  public static JsonSerializerOptions CreateJsonOptions()
  {
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    options.Converters.Add(new JsonStringEnumConverter());
    return options;
  }

  void EnsureOwnedDirectory()
  {
    for (var parent = new DirectoryInfo(Directory); parent != null; parent = parent.Parent)
      if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
        throw new InvalidOperationException("The dashboard system directory must not use symbolic links or junctions.");
    if (System.IO.Directory.Exists(Directory) && !File.Exists(Path.Combine(Directory, Marker))
        && System.IO.Directory.EnumerateFileSystemEntries(Directory).Any())
      throw new InvalidOperationException($"The dashboard system directory '{Directory}' contains files it did not create. " +
          "Choose an empty directory with --Dashboard:SystemDirectory=<path>.");
    System.IO.Directory.CreateDirectory(Directory);
    File.WriteAllText(Path.Combine(Directory, Marker), "ZoneTree.Analytics.Dashboard users, sessions and settings");
  }

  public bool TryGet<T>(string key, out T value)
  {
    if (tree.TryGet(key, out var json) && json != null)
    {
      value = JsonSerializer.Deserialize<T>(json, Json);
      return true;
    }
    value = default;
    return false;
  }

  public T Get<T>(string key) => TryGet<T>(key, out var value) ? value : default;

  public bool Contains(string key) => tree.TryGet(key, out var json) && json != null;

  public void Put<T>(string key, T value) => tree.Upsert(key, JsonSerializer.Serialize(value, Json));

  public void Delete(string key) => tree.ForceDelete(key);

  // The keys under the prefix, in key order, without reading their values.
  public List<string> Keys(string prefix)
  {
    var result = new List<string>();
    using var iterator = tree.CreateIterator();
    iterator.Seek(prefix);
    while (iterator.Next() && iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
      result.Add(iterator.CurrentKey);
    return result;
  }

  // Every record under the prefix, in key order.
  public List<KeyValuePair<string, T>> List<T>(string prefix)
  {
    var result = new List<KeyValuePair<string, T>>();
    using var iterator = tree.CreateIterator();
    iterator.Seek(prefix);
    while (iterator.Next() && iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
      result.Add(new(iterator.CurrentKey, JsonSerializer.Deserialize<T>(iterator.CurrentValue, Json)));
    return result;
  }

  // Up to take records under the prefix in descending key order, starting below before (exclusive)
  // or at the last key.
  public List<KeyValuePair<string, T>> ListDescending<T>(string prefix, string before, int take)
  {
    var result = new List<KeyValuePair<string, T>>();
    using var iterator = tree.CreateReverseIterator();
    iterator.Seek(before ?? prefix + char.MaxValue);
    while (result.Count < take && iterator.Next() && iterator.CurrentKey.StartsWith(prefix, StringComparison.Ordinal))
    {
      if (before != null && string.CompareOrdinal(iterator.CurrentKey, before) >= 0) continue;
      result.Add(new(iterator.CurrentKey, JsonSerializer.Deserialize<T>(iterator.CurrentValue, Json)));
    }
    return result;
  }

  public void Dispose()
  {
    maintainer.Dispose();
    tree.Dispose();
  }
}
