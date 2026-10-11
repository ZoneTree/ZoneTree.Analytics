using System.Runtime.ExceptionServices;

namespace ZoneTree.Analytics.Internal;

/// <summary>Attempts every independent resource, retaining ownership of failed disposals.</summary>
static class Disposal
{
  public static void DisposeAll(List<IDisposable> resources)
  {
    var failures = new List<Exception>();
    for (var i = resources.Count - 1; i >= 0; --i)
    {
      try
      {
        resources[i].Dispose();
        resources.RemoveAt(i);
      }
      catch (Exception exception)
      {
        failures.Add(exception);
      }
    }
    ThrowFailures(failures);
  }

  public static void ThrowFailures(List<Exception> failures)
  {
    if (failures.Count == 1)
      ExceptionDispatchInfo.Throw(failures[0]);
    if (failures.Count > 1)
      throw new AggregateException("Storage cleanup failed.", failures);
  }
}

/// <summary>Releases dependent resources in reverse acquisition order, resuming on retry.</summary>
sealed class DisposalGroup : IDisposable
{
  readonly List<IDisposable> resources = [];

  public void Add(IDisposable resource) => resources.Add(resource);

  public void Dispose()
  {
    while (resources.Count > 0)
    {
      resources[^1].Dispose();
      resources.RemoveAt(resources.Count - 1);
    }
  }
}
