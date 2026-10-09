namespace ZoneTree.Analytics;

/// <summary>
/// Ingestion pipeline options.
/// </summary>
public sealed class IngestionOptions
{
  /// <summary>
  /// Maximum number of queued events per worker. Default is 100,000.
  /// </summary>
  public int QueueCapacity { get; set; } = 100_000;

  /// <summary>
  /// Number of background ingestion workers. Events are routed to workers by
  /// visitor, so each visitor's events are processed in order. Default is 1,
  /// which handles well over a hundred thousand events per second.
  /// </summary>
  public int WorkerCount { get; set; } = 1;

  /// <summary>
  /// How often buffered aggregates and session states are written to storage.
  /// Rollup-based queries lag behind ingestion by at most this interval.
  /// Default is 2 seconds.
  /// </summary>
  public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Forces a flush when a worker buffers more distinct rollup rows than this.
  /// Default is 100,000.
  /// </summary>
  public int MaxBufferedRollupRows { get; set; } = 100_000;

  /// <summary>
  /// Behavior of <see cref="AnalyticsEngine.Track"/> when the queue is full.
  /// Default is <see cref="QueueFullBehavior.Drop"/>, so request threads never block.
  /// </summary>
  public QueueFullBehavior QueueFullBehavior { get; set; } = QueueFullBehavior.Drop;

  /// <summary>
  /// Events with a timestamp further in the future than this are rejected.
  /// Default is 1 hour.
  /// </summary>
  public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromHours(1);

  internal void Validate()
  {
    if (QueueCapacity < 1)
      throw new ArgumentException("Ingestion.QueueCapacity must be positive.");
    if (WorkerCount is < 1 or > 64)
      throw new ArgumentException("Ingestion.WorkerCount must be between 1 and 64.");
    if (FlushInterval <= TimeSpan.Zero)
      throw new ArgumentException("Ingestion.FlushInterval must be positive.");
    if (MaxBufferedRollupRows < 1)
      throw new ArgumentException("Ingestion.MaxBufferedRollupRows must be positive.");
    if (MaxFutureSkew < TimeSpan.Zero)
      throw new ArgumentException("Ingestion.MaxFutureSkew cannot be negative.");
    if (!Enum.IsDefined(QueueFullBehavior))
      throw new ArgumentException("Ingestion.QueueFullBehavior is not a valid value.");
  }
}

/// <summary>
/// Behavior when the ingestion queue is full.
/// </summary>
public enum QueueFullBehavior
{
  /// <summary>The event is dropped and counted in <see cref="AnalyticsStatistics.EventsDropped"/>.</summary>
  Drop,

  /// <summary>The caller blocks until queue space is available.</summary>
  Wait,
}
