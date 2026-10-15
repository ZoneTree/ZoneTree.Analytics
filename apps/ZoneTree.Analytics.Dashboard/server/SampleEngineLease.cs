using ZoneTree.Analytics;

sealed class SampleEngineLease(AnalyticsEngine engine, Action release) : IDisposable
{
  public AnalyticsEngine Engine { get; } = engine;
  Action onRelease = release;
  public void Dispose() => Interlocked.Exchange(ref onRelease, null)?.Invoke();
}

sealed class SampleDataBusyException : Exception;
sealed class BenchmarkDataUnavailableException : Exception;

sealed class SampleRequestServices(IServiceProvider original, AnalyticsEngine engine) : IServiceProvider
{
  public object GetService(Type serviceType) => serviceType == typeof(AnalyticsEngine) ? engine : original.GetService(serviceType);
}
