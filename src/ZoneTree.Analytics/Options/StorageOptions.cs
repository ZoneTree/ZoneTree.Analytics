using System.Globalization;
using ZoneTree.Logger;

namespace ZoneTree.Analytics;

/// <summary>
/// Storage options.
/// </summary>
public sealed class StorageOptions
{
  /// <summary>
  /// Stores every enriched event. Raw events answer ad-hoc queries over any dimension
  /// combination, real-time visitor counts and visitor erasure requests.
  /// When disabled, only rollups are kept. Default is true.
  /// </summary>
  public bool StoreRawEvents { get; set; } = true;

  /// <summary>
  /// How many ZoneTree partitions each data tier is split into over time. It is persisted when
  /// the data directory is created and cannot be changed afterwards.
  /// </summary>
  public PartitioningOptions Partitioning { get; set; } = new();

  /// <summary>
  /// ZoneTree settings of raw event partitions. Raw events are only scanned, never point-read,
  /// so by default the mutable segment holds 50,000 events and has no Bloom filter.
  /// </summary>
  public StoreOptions RawEvents { get; private set; } = new()
  {
    MutableSegmentMaxItemCount = 50_000,
    MutableSegmentBloomFilterBitsPerItem = 0
  };

  /// <summary>
  /// ZoneTree settings of rollup partitions. Rollup rows carry visitor sketches, so by default
  /// the mutable segment is kept small: 20,000 rows. Rows that are still being updated are
  /// rewritten into the mutable segment, so lowering it mainly moves cold rows to disk sooner.
  /// </summary>
  public StoreOptions Rollups { get; private set; } = new() { MutableSegmentMaxItemCount = 20_000 };

  /// <summary>
  /// ZoneTree settings of the active session store. By default the mutable segment holds
  /// 50,000 sessions.
  /// </summary>
  public StoreOptions Sessions { get; private set; } = new() { MutableSegmentMaxItemCount = 50_000 };

  /// <summary>
  /// A separate root directory for write-ahead logs, for example on a faster disk. Each store
  /// keeps its log under the same relative path as its data. Null (the default) keeps logs next
  /// to the data. Engine metadata always keeps its log next to its data. It is persisted when
  /// the data directory is created and cannot be changed afterwards, because unflushed records
  /// live in the log.
  /// </summary>
  public string WriteAheadLogDirectory { get; set; }

  /// <summary>
  /// Partitions that were not used for this long are closed. Default is 10 minutes.
  /// </summary>
  public TimeSpan PartitionIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

  /// <summary>
  /// The logger used by the engine and its ZoneTree stores.
  /// Default is a console logger at warning level.
  /// </summary>
  public ILogger Logger { get; set; }

  internal StorageOptions Clone()
  {
    var clone = (StorageOptions)MemberwiseClone();
    clone.Partitioning = Partitioning?.Clone();
    clone.RawEvents = RawEvents.Clone();
    clone.Rollups = Rollups.Clone();
    clone.Sessions = Sessions.Clone();
    return clone;
  }

  internal void Validate(RetentionOptions retention, Granularity granularities)
  {
    if (PartitionIdleTimeout <= TimeSpan.Zero)
      throw new ArgumentException("Storage.PartitionIdleTimeout must be positive.");
    if (WriteAheadLogDirectory != null && string.IsNullOrWhiteSpace(WriteAheadLogDirectory))
      throw new ArgumentException("Storage.WriteAheadLogDirectory cannot be empty; use null to keep logs next to the data.");
    RawEvents.Validate(nameof(RawEvents));
    Rollups.Validate(nameof(Rollups));
    Sessions.Validate(nameof(Sessions));
    Partitioning ??= new();
    Partitioning.Validate(retention, StoreRawEvents, granularities);
  }
}

/// <summary>
/// Time partitioning of each data tier. Every partition is a separate ZoneTree.
/// </summary>
/// <remarks>
/// <para>
/// Retention drops whole partitions once they end before the retention cutoff, so data is kept
/// for up to one partition period longer than its retention. Smaller partitions make retention
/// more precise, keep individual trees and their merges smaller and make drops cheaper; larger
/// partitions mean fewer trees, files and directories, and fewer trees per long-range query.
/// </para>
/// <para>
/// Validation rejects a partition period longer than the retention of its tier when a shorter
/// period exists, and daily partitions of daily rollups, which would hold a single bucket each.
/// Partitioning is persisted when the data directory is created; reopening with different
/// values throws <see cref="AnalyticsConfigurationException"/>.
/// </para>
/// </remarks>
public sealed class PartitioningOptions
{
  /// <summary>
  /// Partition period of raw events (UTC). Default is <see cref="PartitionPeriod.Month"/>;
  /// use <see cref="PartitionPeriod.Day"/> for high-volume sites or short raw event retention.
  /// </summary>
  public PartitionPeriod RawEvents { get; set; } = PartitionPeriod.Month;

  /// <summary>
  /// Partition period of minute rollups (UTC). Default is <see cref="PartitionPeriod.Day"/>.
  /// </summary>
  public PartitionPeriod MinuteRollups { get; set; } = PartitionPeriod.Day;

  /// <summary>
  /// Partition period of hourly rollups (UTC). Default is <see cref="PartitionPeriod.Month"/>.
  /// </summary>
  public PartitionPeriod HourRollups { get; set; } = PartitionPeriod.Month;

  /// <summary>
  /// Partition period of daily rollups, in the reporting time zone. <see cref="PartitionPeriod.Month"/>
  /// or <see cref="PartitionPeriod.Year"/>. Default is <see cref="PartitionPeriod.Year"/>.
  /// </summary>
  public PartitionPeriod DayRollups { get; set; } = PartitionPeriod.Year;

  internal PartitioningOptions Clone() => (PartitioningOptions)MemberwiseClone();

  internal void Validate(RetentionOptions retention, bool storeRawEvents, Granularity granularities)
  {
    Validate(nameof(RawEvents), RawEvents, retention.RawEvents, storeRawEvents);
    Validate(nameof(MinuteRollups), MinuteRollups, retention.MinuteRollups, (granularities & Granularity.Minute) != 0);
    Validate(nameof(HourRollups), HourRollups, retention.HourRollups, (granularities & Granularity.Hour) != 0);
    Validate(nameof(DayRollups), DayRollups, retention.DayRollups, (granularities & Granularity.Day) != 0);
    if (DayRollups == PartitionPeriod.Day)
      throw new ArgumentException(
          "Storage.Partitioning.DayRollups must be Month or Year: a daily partition would hold a single daily bucket.");
  }

  static void Validate(string tier, PartitionPeriod period, TimeSpan? retention, bool enabled)
  {
    if (!Enum.IsDefined(period))
      throw new ArgumentException($"Storage.Partitioning.{tier} is not a valid partition period.");
    if (!enabled || retention == null || period == PartitionPeriod.Day)
      return;
    var shortest = period == PartitionPeriod.Month ? TimeSpan.FromDays(28) : TimeSpan.FromDays(365);
    if (retention.Value < shortest)
    {
      var unit = period.ToString().ToLowerInvariant();
      throw new ArgumentException(string.Create(
          CultureInfo.InvariantCulture,
          $"Retention.{tier} ({retention.Value.TotalDays:0.##} days) is shorter than one {unit} partition. " +
          $"Retention drops whole partitions, so data would be kept up to a {unit} longer than intended. " +
          $"Use a shorter Storage.Partitioning.{tier}."));
    }
  }
}
