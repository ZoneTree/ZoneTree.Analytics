using System.Globalization;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// When each rollup definition and each opt-in rollup aggregate became active.
/// </summary>
/// <remarks>
/// A definition or aggregate added to an existing data directory, or re-added after it was
/// removed, only covers data recorded after it became active; queries of earlier ranges read raw
/// events. One that is no longer configured is forgotten, so adding it again starts a new
/// activation instead of claiming coverage of the time it was missing.
/// </remarks>
sealed class RollupActivations
{
  const string DefinitionPrefix = "rollup:";

  const string AggregatePrefix = "aggregate:";

  const string DistinctPrefix = "distinct:";

  readonly Dictionary<string, long> definitions = new(StringComparer.Ordinal);

  readonly Dictionary<string, long> aggregates = new(StringComparer.Ordinal);

  /// <summary>
  /// Records the activations of the configured rollups and loads them.
  /// </summary>
  public static RollupActivations Register(MetaStore meta, RollupOptions rollups, bool isNewDirectory)
  {
    var definitionKeys = rollups.Definitions.ToDictionary(x => x.Id, DefinitionKey, StringComparer.Ordinal);
    var aggregateKeys = new List<string>();
    foreach (var (valueName, configured) in rollups.ValueAggregates)
    {
      foreach (var aggregate in Flags(configured))
        aggregateKeys.Add(AggregateKey(valueName, aggregate));
    }
    aggregateKeys.AddRange(rollups.DistinctDimensions.Select(DistinctKey));
    var configuredKeys = definitionKeys.Values.Concat(aggregateKeys).ToHashSet(StringComparer.Ordinal);
    var activation = (isNewDirectory ? 0 : DateTime.UtcNow.Ticks).ToString(CultureInfo.InvariantCulture);
    meta.Update(entries =>
    {
      foreach (var key in entries.Keys.Where(IsActivationKey).ToList())
      {
        if (!configuredKeys.Contains(key))
          entries.Remove(key);
      }
      foreach (var key in configuredKeys)
        entries.TryAdd(key, activation);
    });
    var result = new RollupActivations();
    foreach (var (id, key) in definitionKeys)
      result.definitions[id] = ReadTicks(meta, key);
    foreach (var key in aggregateKeys)
      result.aggregates[key] = ReadTicks(meta, key);
    return result;
  }

  static long ReadTicks(MetaStore meta, string key) => long.Parse(meta.Get(key), CultureInfo.InvariantCulture);

  static bool IsActivationKey(string key) =>
      key.StartsWith(DefinitionPrefix, StringComparison.Ordinal) ||
      key.StartsWith(AggregatePrefix, StringComparison.Ordinal) ||
      key.StartsWith(DistinctPrefix, StringComparison.Ordinal);

  /// <summary>
  /// The key of a definition's activation. It covers everything that decides which rows the
  /// definition holds: its dimensions and its event restriction. Changing either starts a new
  /// activation.
  /// </summary>
  static string DefinitionKey(RollupDefinition definition)
  {
    var key = DefinitionPrefix + definition.Id;
    if (definition.Events == null)
      return key;
    var events = definition.Events.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    return key + KeyCodec.ValueSeparator + "events:" + string.Join(KeyCodec.ValueSeparator, events);
  }

  static string AggregateKey(string valueName, ValueAggregates aggregate) =>
      AggregatePrefix + (aggregate == ValueAggregates.Percentiles ? "percentiles:" : "first-last:") + valueName;

  static string DistinctKey(string dimension) => DistinctPrefix + dimension;

  static IEnumerable<ValueAggregates> Flags(ValueAggregates aggregates)
  {
    if ((aggregates & ValueAggregates.Percentiles) != 0)
      yield return ValueAggregates.Percentiles;
    if ((aggregates & ValueAggregates.FirstAndLast) != 0)
      yield return ValueAggregates.FirstAndLast;
  }

  /// <summary>
  /// Since when a definition covers data; <see cref="long.MaxValue"/> when it is not configured.
  /// </summary>
  public long DefinitionSince(string id) =>
      definitions.TryGetValue(id, out var since) ? since : long.MaxValue;

  /// <summary>
  /// Since when rollups hold the given aggregates of a value; <see cref="long.MaxValue"/> when
  /// one of them is not configured.
  /// </summary>
  public long AggregatesSince(string valueName, ValueAggregates required)
  {
    long since = 0;
    foreach (var aggregate in Flags(required))
      since = Math.Max(since, aggregates.TryGetValue(AggregateKey(valueName, aggregate), out var at) ? at : long.MaxValue);
    return since;
  }

  /// <summary>
  /// Since when rollups count the distinct values of a dimension; <see cref="long.MaxValue"/>
  /// when it is not configured.
  /// </summary>
  public long DistinctSince(string dimension) =>
      aggregates.TryGetValue(DistinctKey(dimension), out var since) ? since : long.MaxValue;
}
