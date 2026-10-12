namespace ZoneTree.Analytics.AspNetCore;

sealed class ReportingRequestLimiter(AnalyticsWebOptions options) : IDisposable
{
  readonly SemaphoreSlim slots = new(options.MaxConcurrentReportingRequests, options.MaxConcurrentReportingRequests);

  public bool TryEnter() => slots.Wait(0);

  public void Release() => slots.Release();

  public void Dispose() => slots.Dispose();
}
