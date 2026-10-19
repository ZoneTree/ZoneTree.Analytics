using ZoneTree.Analytics;

sealed class SampleEngineLease(AnalyticsEngine engine, Action release) : IDisposable
{
  public AnalyticsEngine Engine { get; } = engine;
  Action onRelease = release;
  public void Dispose() => Interlocked.Exchange(ref onRelease, null)?.Invoke();
}

sealed class SampleDataBusyException : Exception;
sealed class BenchmarkDataUnavailableException : Exception;
