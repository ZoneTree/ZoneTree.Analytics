namespace ZoneTree.Analytics;

/// <summary>
/// Records analytics events.
/// </summary>
/// <remarks>
/// <see cref="AnalyticsEngine"/> implements this interface. Application code that only tracks
/// events can depend on it instead of the engine, so tests can replace it with a fake.
/// Queries are best tested against a real engine on a temporary directory: it is embedded and
/// opens in milliseconds.
/// </remarks>
public interface IAnalyticsTracker
{
  /// <summary>
  /// Queues an event for processing and returns immediately.
  /// </summary>
  /// <remarks>
  /// The event is copied, so it can be modified or reused once this method returns.
  /// <see cref="AnalyticsOptions.EventFilters"/> run here, on the calling thread. When the queue
  /// is full, the call blocks until there is space with <see cref="QueueFullBehavior.Wait"/> and
  /// drops the event with <see cref="QueueFullBehavior.Drop"/>.
  /// </remarks>
  /// <param name="analyticsEvent">The event.</param>
  /// <returns>
  /// True if the event was queued. False if it was filtered out, the engine is disposed, or
  /// the queue is full and <see cref="QueueFullBehavior.Drop"/> is configured.
  /// </returns>
  /// <exception cref="ArgumentException">The event name is missing or reserved.</exception>
  bool Track(AnalyticsEvent analyticsEvent);

  /// <summary>
  /// Queues an event, waiting asynchronously for queue space when the queue is full.
  /// </summary>
  /// <remarks>
  /// The event is copied, so it can be modified or reused once the returned task completes.
  /// </remarks>
  /// <param name="analyticsEvent">The event.</param>
  /// <param name="cancellationToken">Cancels waiting for queue space.</param>
  /// <returns>True if the event was queued; false if it was filtered out or the engine is disposed.</returns>
  /// <exception cref="ArgumentException">The event name is missing or reserved.</exception>
  ValueTask<bool> TrackAsync(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken = default);
}
