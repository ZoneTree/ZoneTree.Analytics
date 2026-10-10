using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Ingestion;

/// <summary>
/// Identifies one buffered rollup row.
/// </summary>
readonly struct RollupBufferKey : IEquatable<RollupBufferKey>
{
  public readonly Granularity Granularity;

  public readonly long Bucket;

  public readonly string Site;

  public readonly string Event;

  public readonly int Definition;

  public readonly string Value;

  readonly int hash;

  public RollupBufferKey(
      Granularity granularity, long bucket, string site, string eventName, int definition, string value)
      : this(granularity, bucket, site, eventName, definition, value,
          HashCode.Combine(site, eventName), value.GetHashCode())
  {
  }

  /// <summary>
  /// Creates a key from precomputed string hashes, so hot paths hash each string once.
  /// </summary>
  public RollupBufferKey(
      Granularity granularity,
      long bucket,
      string site,
      string eventName,
      int definition,
      string value,
      int siteEventHash,
      int valueHash)
  {
    hash = HashCode.Combine(siteEventHash, valueHash, definition, (int)granularity, bucket);
    Granularity = granularity;
    Bucket = bucket;
    Site = site;
    Event = eventName;
    Definition = definition;
    Value = value;
  }

  public bool Equals(RollupBufferKey other) =>
      Granularity == other.Granularity &&
      Bucket == other.Bucket &&
      Definition == other.Definition &&
      string.Equals(Value, other.Value, StringComparison.Ordinal) &&
      string.Equals(Event, other.Event, StringComparison.Ordinal) &&
      string.Equals(Site, other.Site, StringComparison.Ordinal);

  public override bool Equals(object obj) => obj is RollupBufferKey other && Equals(other);

  public override int GetHashCode() => hash;
}

/// <summary>
/// Which rollups a contribution can reach. Engine-generated rows carry a known, limited set of
/// dimensions and skip rollups over dimensions they never have.
/// </summary>
enum DimensionScope
{
  /// <summary>Session-level dimensions and the entry page only (session rows).</summary>
  Session,

  /// <summary>Session scope plus page and hostname (entrance and exit rows).</summary>
  PageView,

  /// <summary>Page, hostname, original event, bot name and category (bot rows).</summary>
  Bot,

  /// <summary>Any dimension (tracked events).</summary>
  All,
}

/// <summary>
/// One value change applied to a rollup row.
/// </summary>
readonly record struct ValueChange(string Name, double Value, bool IsDelta);

/// <summary>
/// Pre-aggregates rollup rows in memory between flushes. Owned by one worker.
/// </summary>
/// <remarks>
/// Buffering turns many events into one storage merge per distinct row and flush,
/// which keeps ZoneTree write volume proportional to the number of distinct rows
/// rather than the number of events.
/// </remarks>
sealed class RollupBuffer
{
  readonly RollupDefinition[] definitions;

  readonly Granularity[] granularities;

  readonly LocalDayCache dayCache;

  readonly int precision;

  readonly string[] valueParts = new string[16];

  Dictionary<RollupBufferKey, RollupAccumulator> rows = [];

  readonly Dictionary<string, ValueAggregates> valueAggregates;

  readonly string[] distinctDimensions;

  // The opt-in aggregates of the current event, computed once for all of its rows.
  readonly List<(string Name, double Value, ValueAggregates Aggregates, QuantileBucket Bucket)> eventDetails = [];

  readonly List<(string Dimension, ulong Hash)> eventDistinct = [];

  // Per scope, per definition: whether contributions of the scope reach the definition.
  readonly bool[][] accepts;

  public RollupBuffer(
      RollupDefinition[] definitions,
      Granularity enabled,
      ReportingCalendar calendar,
      int precision,
      SessionSchema schema,
      IReadOnlyDictionary<string, ValueAggregates> valueAggregates,
      IReadOnlyCollection<string> distinctDimensions)
  {
    this.definitions = definitions;
    this.valueAggregates = valueAggregates
        .Where(x => x.Value != ValueAggregates.None)
        .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    this.distinctDimensions = [.. distinctDimensions];
    granularities = RollupStore.Granularities.Where(x => (enabled & x) != 0).ToArray();
    dayCache = new LocalDayCache(calendar);
    this.precision = precision;
    accepts = Enum.GetValues<DimensionScope>()
        .Select(scope => definitions
            .Select(x => schema.Accepts(x, scope, scope == DimensionScope.Bot ? StandardEvents.Bot : null))
            .ToArray())
        .ToArray();
  }

  public int Count => rows.Count;

  /// <summary>
  /// Rows older than these cutoffs are not buffered. Refreshed by the owning worker.
  /// </summary>
  public RetentionCutoffs Cutoffs { get; set; } = RetentionCutoffs.None;

  /// <summary>
  /// Returns the buffered rows and starts a new buffer.
  /// </summary>
  public Dictionary<RollupBufferKey, RollupAccumulator> Swap()
  {
    var current = rows;
    rows = [];
    return current;
  }

  long BucketOf(Granularity granularity, long ticks) => granularity switch
  {
    Granularity.Minute => ReportingCalendar.FloorTicks(ticks, TimeSpan.TicksPerMinute),
    Granularity.Hour => ReportingCalendar.FloorTicks(ticks, TimeSpan.TicksPerHour),
    _ => dayCache.LocalDay(ticks)
  };

  /// <summary>
  /// Adds a contribution to every applicable rollup row.
  /// A rollup applies when the event name matches and every one of its dimensions is present.
  /// </summary>
  public void Contribute(
      string site,
      string eventName,
      long ticks,
      IReadOnlyDictionary<string, string> dimensions,
      long count,
      ulong visitor,
      List<ValueChange> changes,
      DimensionScope scope = DimensionScope.All)
  {
    if (granularities.Length == 0)
      return;
    var siteEventHash = HashCode.Combine(site, eventName);
    Span<long> buckets = stackalloc long[granularities.Length];
    var kept = 0;
    for (var g = 0; g < granularities.Length; ++g)
    {
      var bucket = BucketOf(granularities[g], ticks);
      // A bucket beyond its granularity's retention is skipped (long.MinValue).
      if (Cutoffs.KeepsRollup(granularities[g], ticks, bucket))
      {
        buckets[g] = bucket;
        ++kept;
      }
      else
      {
        buckets[g] = long.MinValue;
      }
    }
    if (kept == 0)
      return;
    PrepareOptInAggregates(dimensions, changes);

    var scopeAccepts = accepts[(int)scope];
    for (var d = 0; d < definitions.Length; ++d)
    {
      var definition = definitions[d];
      if (!scopeAccepts[d])
        continue;
      if (definition.Events != null && !definition.Events.Contains(eventName))
        continue;
      var value = BuildValue(definition, dimensions);
      var valueHash = value.GetHashCode();
      for (var g = 0; g < granularities.Length; ++g)
      {
        if (buckets[g] == long.MinValue)
          continue;
        var key = new RollupBufferKey(granularities[g], buckets[g], site, eventName, d, value, siteEventHash, valueHash);
        if (!rows.TryGetValue(key, out var accumulator))
        {
          accumulator = new RollupAccumulator();
          rows.Add(key, accumulator);
        }
        accumulator.Count += count;
        if (visitor != 0)
          accumulator.AddVisitor(visitor, precision);
        if (changes != null)
        {
          foreach (var change in changes)
          {
            if (change.IsDelta)
              accumulator.AddValueDelta(change.Name, change.Value);
            else
              accumulator.AddValue(change.Name, change.Value);
          }
        }
        foreach (var detail in eventDetails)
          accumulator.AddValueDetail(detail.Name, detail.Value, ticks, detail.Aggregates, detail.Bucket);
        foreach (var (dimension, hash) in eventDistinct)
          accumulator.AddDistinct(dimension, hash, precision);
      }
    }
  }

  /// <summary>
  /// Collects the opt-in aggregates of an event: configured value aggregates and distinct
  /// dimension values. Without configuration, this costs two length checks.
  /// </summary>
  void PrepareOptInAggregates(IReadOnlyDictionary<string, string> dimensions, List<ValueChange> changes)
  {
    eventDetails.Clear();
    if (valueAggregates.Count > 0 && changes != null)
    {
      foreach (var change in changes)
      {
        if (change.IsDelta || !valueAggregates.TryGetValue(change.Name, out var aggregates))
          continue;
        var bucket = (aggregates & ValueAggregates.Percentiles) != 0 ? QuantileSketch.BucketOf(change.Value) : default;
        eventDetails.Add((change.Name, change.Value, aggregates, bucket));
      }
    }
    eventDistinct.Clear();
    foreach (var dimension in distinctDimensions)
    {
      if (dimensions.TryGetValue(dimension, out var value))
        eventDistinct.Add((dimension, Text.StableHash(value)));
    }
  }

  /// <summary>
  /// Builds the stored value of a row. A dimension the event does not carry is stored as
  /// <see cref="KeyCodec.MissingValue"/>, so every event is counted in every rollup and
  /// grouped totals always equal ungrouped totals.
  /// </summary>
  string BuildValue(RollupDefinition definition, IReadOnlyDictionary<string, string> dimensions)
  {
    var names = definition.Dimensions;
    switch (names.Count)
    {
      case 0:
        return "";
      case 1:
        return dimensions.TryGetValue(names[0], out var single) ? single : KeyCodec.MissingValue;
    }
    var parts = names.Count <= valueParts.Length ? valueParts : new string[names.Count];
    for (var i = 0; i < names.Count; ++i)
      parts[i] = dimensions.TryGetValue(names[i], out var part) ? part : KeyCodec.MissingValue;
    return string.Join(KeyCodec.ValueSeparator, parts, 0, names.Count);
  }
}
