using ZoneTree.Analytics.Internal;
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

static class DimensionScopes
{
  /// <summary>
  /// True when rows of the scope can carry the dimension.
  /// </summary>
  public static bool Contains(DimensionScope scope, string dimension) => scope switch
  {
    DimensionScope.Session =>
        dimension == StandardDimensions.EntryPage || EnrichmentPipeline.IsSessionLevel(dimension),
    DimensionScope.PageView =>
        dimension is StandardDimensions.EntryPage or StandardDimensions.Page or StandardDimensions.Hostname ||
        EnrichmentPipeline.IsSessionLevel(dimension),
    DimensionScope.Bot => StandardDimensions.BotLevel.Contains(dimension),
    _ => true
  };
}

/// <summary>
/// One metric change applied to a rollup row.
/// </summary>
readonly record struct MetricChange(string Name, double Value, bool IsDelta);

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

  public RollupBuffer(
      RollupDefinition[] definitions,
      Granularity enabled,
      ReportingCalendar calendar,
      int precision)
  {
    this.definitions = definitions;
    granularities = new[] { Granularity.Minute, Granularity.Hour, Granularity.Day }
        .Where(x => (enabled & x) != 0)
        .ToArray();
    dayCache = new LocalDayCache(calendar);
    this.precision = precision;
  }

  public int Count => rows.Count;

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
      IReadOnlyList<MetricChange> metrics,
      DimensionScope scope = DimensionScope.All)
  {
    if (granularities.Length == 0)
      return;
    var siteEventHash = HashCode.Combine(site, eventName);
    Span<long> buckets = stackalloc long[granularities.Length];
    for (var g = 0; g < granularities.Length; ++g)
      buckets[g] = BucketOf(granularities[g], ticks);

    for (var d = 0; d < definitions.Length; ++d)
    {
      var definition = definitions[d];
      if (!definition.Accepts(scope, eventName))
        continue;
      if (definition.Events != null && !definition.Events.Contains(eventName))
        continue;
      var value = BuildValue(definition, dimensions);
      var valueHash = value.GetHashCode();
      for (var g = 0; g < granularities.Length; ++g)
      {
        var key = new RollupBufferKey(granularities[g], buckets[g], site, eventName, d, value, siteEventHash, valueHash);
        if (!rows.TryGetValue(key, out var accumulator))
        {
          accumulator = new RollupAccumulator();
          rows.Add(key, accumulator);
        }
        accumulator.Count += count;
        if (visitor != 0)
          accumulator.AddVisitor(visitor, precision);
        if (metrics == null)
          continue;
        foreach (var metric in metrics)
        {
          if (metric.IsDelta)
            accumulator.AddMetricDelta(metric.Name, metric.Value);
          else
            accumulator.AddMetric(metric.Name, metric.Value);
        }
      }
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
