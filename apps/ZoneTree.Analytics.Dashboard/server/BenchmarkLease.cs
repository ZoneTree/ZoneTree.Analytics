using ZoneTree.Analytics;

sealed class BenchmarkLease(AnalyticsEngine engine, Action release) : IDisposable
{
  public AnalyticsEngine Engine { get; } = engine;
  Action onRelease = release;
  public void Dispose() => Interlocked.Exchange(ref onRelease, null)?.Invoke();
}

sealed class BenchmarkDataBusyException : Exception;
sealed class BenchmarkDataUnavailableException : Exception;
