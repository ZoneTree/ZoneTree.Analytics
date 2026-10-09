using System.Globalization;
using ZoneTree.Logger;
using ZoneTree.Options;

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
  /// The ZoneTree write-ahead-log mode of every store. Default is
  /// <see cref="WriteAheadLogMode.AsyncCompressed"/>.
  /// </summary>
  public WriteAheadLogMode WriteAheadLogMode { get; set; } = WriteAheadLogMode.AsyncCompressed;

  /// <summary>
  /// Mutable segment size of raw event partitions: how many recent events are held in
  /// memory before they move toward disk. Default is 50,000.
  /// </summary>
  public int RawEventsMutableSegmentMaxItemCount { get; set; } = 50_000;

  /// <summary>
  /// Mutable segment size of rollup partitions. Rollup rows carry visitor sketches, so the
  /// default is kept small: 20,000. Rows that are still being updated are rewritten into
  /// the mutable segment, so lowering this mainly moves cold rows to disk sooner.
  /// </summary>
  public int RollupsMutableSegmentMaxItemCount { get; set; } = 20_000;

  /// <summary>
  /// Partitions that were not used for this long are closed. Default is 10 minutes.
  /// </summary>
  public TimeSpan PartitionIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

  /// <summary>
  /// Optional hook to tune ZoneTree disk segment options of every store.
  /// </summary>
  public Action<DiskSegmentOptions> ConfigureDiskSegments { get; set; }

  /// <summary>
  /// Optional hook to tune ZoneTree write-ahead-log options of every store.
  /// </summary>
  public Action<WriteAheadLogOptions> ConfigureWriteAheadLog { get; set; }

  /// <summary>
  /// Optional hook to tune the ZoneTree maintainer of every data store (raw event and rollup
  /// partitions and the session store), for example merge thresholds or block cache cleanup.
  /// It runs when a store or partition is opened.
  /// </summary>
  public Action<IMaintainer> ConfigureMaintainer { get; set; }

  /// <summary>
  /// The logger used by the engine and its ZoneTree stores.
  /// Default is a console logger at warning level.
  /// </summary>
  public ILogger Logger { get; set; }

  internal void Validate(RetentionOptions retention, Granularity granularities)
  {
    if (!Enum.IsDefined(WriteAheadLogMode))
      throw new ArgumentException("Storage.WriteAheadLogMode is not a valid value.");
    if (RawEventsMutableSegmentMaxItemCount < 1)
      throw new ArgumentException("Storage.RawEventsMutableSegmentMaxItemCount must be positive.");
    if (RollupsMutableSegmentMaxItemCount < 1)
      throw new ArgumentException("Storage.RollupsMutableSegmentMaxItemCount must be positive.");
    if (PartitionIdleTimeout <= TimeSpan.Zero)
      throw new ArgumentException("Storage.PartitionIdleTimeout must be positive.");
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
