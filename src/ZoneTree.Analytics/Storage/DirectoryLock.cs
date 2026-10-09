namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Holds a data directory exclusively for the lifetime of one engine, across processes.
/// </summary>
/// <remarks>
/// The lock is an open handle on <c>engine.lock</c> that shares nothing: Windows refuses a second
/// open, and .NET takes an exclusive advisory lock on Unix. The operating system releases it when
/// the handle is disposed or the process ends, so a crash never leaves a stale lock behind.
/// </remarks>
sealed class DirectoryLock : IDisposable
{
  readonly FileStream handle;

  public DirectoryLock(string directory)
  {
    var path = Path.Combine(directory, "engine.lock");
    try
    {
      handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException e)
    {
      throw new IOException(
          $"The analytics data directory '{directory}' is in use by another engine. " +
          "Create one engine per data directory and share it.",
          e);
    }
  }

  public void Dispose() => handle.Dispose();
}
