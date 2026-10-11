namespace ZoneTree.Analytics;

/// <summary>
/// A snapshot of engine counters since the engine was opened.
/// </summary>
public sealed class AnalyticsStatistics
{
  /// <summary>Events accepted into the ingestion queue.</summary>
  public long EventsAccepted { get; init; }

  /// <summary>Events dropped because the ingestion queue was full.</summary>
  public long EventsDropped { get; init; }

  /// <summary>Events stored and aggregated.</summary>
  public long EventsProcessed { get; init; }

  /// <summary>Events rejected as invalid or with a timestamp too far in the future.</summary>
  public long EventsRejected { get; init; }

  /// <summary>Events discarded by <see cref="AnalyticsOptions.EventFilters"/>.</summary>
  public long EventsFiltered { get; init; }

  /// <summary>Bot events discarded (<see cref="BotTraffic.Exclude"/>).</summary>
  public long BotsExcluded { get; init; }

  /// <summary>Bot events recorded separately (<see cref="BotTraffic.TrackSeparately"/>).</summary>
  public long BotsTracked { get; init; }

  /// <summary>Sessions started.</summary>
  public long SessionsStarted { get; init; }

  /// <summary>Completed flushes of buffered aggregates.</summary>
  public long Flushes { get; init; }

  /// <summary>
  /// Errors raised while tracking, processing or flushing, including exceptions thrown by extensions and
  /// storage errors such as failed ZoneTree merges or partition cleanups. Details are logged.
  /// </summary>
  public long Errors { get; init; }

  /// <summary>Events currently waiting in ingestion queues.</summary>
  public long QueueLength { get; init; }

  /// <summary>Open ZoneTree partitions.</summary>
  public int OpenPartitions { get; init; }

  /// <summary>
  /// Completed partition deletions reported by retention. Failed or still-leased drops are
  /// counted by a later retention check after cleanup succeeds.
  /// </summary>
  public long PartitionsDropped { get; init; }

  /// <summary>
  /// Every ZoneTree partition of the raw event and rollup tiers, in time order per tier.
  /// </summary>
  public IReadOnlyList<PartitionStatistics> Partitions { get; init; } = [];
}

/// <summary>
/// A ZoneTree partition of a data tier.
/// </summary>
public sealed class PartitionStatistics
{
  /// <summary>
  /// The partition directory relative to the data directory, for example
  /// <c>events/2026-10-11</c>. The last segment is the day the partition starts.
  /// </summary>
  public string Directory { get; init; }

  /// <summary>
  /// The approximate number of records in the partition: deleted records that merges have not
  /// removed yet are included. Null for a partition that was not opened since the engine
  /// started, as counting would open it.
  /// </summary>
  public long? ApproximateRecordCount { get; init; }
}

sealed class Counters
{
  public long EventsAccepted;

  public long EventsDropped;

  public long EventsProcessed;

  public long EventsRejected;

  public long EventsFiltered;

  public long BotsExcluded;

  public long BotsTracked;

  public long SessionsStarted;

  public long Flushes;

  public long Errors;

  public long PartitionsDropped;
}
