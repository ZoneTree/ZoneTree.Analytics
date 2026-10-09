using System.Runtime.ExceptionServices;

namespace ZoneTree.Analytics.Internal;

/// <summary>
/// Runs independent work items on the calling thread and up to <c>maxDegree - 1</c>
/// thread-pool threads.
/// </summary>
/// <remarks>
/// The calling thread takes items too, so the work completes even when the thread pool is busy:
/// a helper that starts after every item is taken exits at once and is not waited for. The first
/// exception skips the items not yet started and is rethrown with its original stack trace.
/// </remarks>
static class ParallelWork
{
  public static void For(int count, int maxDegree, Action<int> body)
  {
    var helpers = Math.Min(maxDegree, count) - 1;
    if (helpers <= 0)
    {
      for (var i = 0; i < count; ++i)
        body(i);
      return;
    }
    var run = new Run(count, body);
    for (var i = 0; i < helpers; ++i)
      ThreadPool.UnsafeQueueUserWorkItem(static run => run.Work(), run, preferLocal: false);
    run.Work();
    run.WaitAndThrow();
  }

  sealed class Run(int count, Action<int> body)
  {
    int next = -1;

    int completed;

    ExceptionDispatchInfo failure;

    public void Work()
    {
      int i;
      while ((i = Interlocked.Increment(ref next)) < count)
      {
        if (Volatile.Read(ref failure) == null)
        {
          try
          {
            body(i);
          }
          catch (Exception e)
          {
            Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(e), null);
          }
        }
        if (Interlocked.Increment(ref completed) == count)
        {
          lock (this)
            Monitor.PulseAll(this);
        }
      }
    }

    public void WaitAndThrow()
    {
      lock (this)
      {
        while (Volatile.Read(ref completed) < count)
          Monitor.Wait(this);
      }
      failure?.Throw();
    }
  }
}
