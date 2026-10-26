using System.Net;
using System.Net.Http.Headers;
using ZoneTree.Analytics.Geo;
using ZoneTree.Logger;

namespace ZoneTree.Analytics;

/// <summary>
/// Resolves IP addresses to locations from database files that it downloads and keeps current.
/// </summary>
/// <remarks>
/// <para>
/// Creating a provider does not wait: database files that exist load in the background, and
/// missing files with a URL are downloaded first. Until a database is loaded, lookups return
/// null; <see cref="WaitForDatabaseAsync"/> waits for it. Every
/// <see cref="IpLocationOptions.UpdateInterval"/>, a conditional request checks each URL for a
/// newer file.
/// </para>
/// <para>
/// A download replaces its file only after it loads as a database, by moving a complete
/// temporary file over it, so readers and other processes sharing the directory never see a
/// partial file. The new database is then loaded and swapped in; lookups never wait for it. A
/// failed download or load is logged and tried again after
/// <see cref="IpLocationOptions.RetryInterval"/>, and the current database stays in use.
/// </para>
/// <para>
/// The engine creates and disposes a provider for <see cref="WebOptions.IpLocation"/>. Create one
/// yourself to share a database between engines, and dispose it after them.
/// </para>
/// </remarks>
public sealed class IpLocationProvider : IGeoLocator, IDisposable, IAsyncDisposable
{
  // Task.Delay accepts at most about 49 days; longer waits are taken in steps.
  static readonly TimeSpan MaxDelay = TimeSpan.FromDays(1);

  // Downloads left behind by a stopped process are removed after this long.
  static readonly TimeSpan AbandonedDownloadAge = TimeSpan.FromDays(1);

  readonly IpLocationOptions options;
  readonly GeoPrecision precision;
  readonly ILogger logger;
  readonly HttpClient client;
  readonly bool ownsClient;
  readonly string[] paths;
  readonly SemaphoreSlim checking = new(1, 1);
  readonly CancellationTokenSource stopping = new();
  readonly TaskCompletionSource available = new(TaskCreationOptions.RunContinuationsAsynchronously);
  // The earliest automatic check of each source, by path: a retry after a failure, the next
  // update after a check. Local time bounds checks whatever the server's clock says.
  readonly Dictionary<string, DateTimeOffset> notBefore = new(StringComparer.Ordinal);
  readonly Task loop;
  IpLocationDatabase database;
  // Set when the files do not load: they are downloaded again in full.
  bool redownload;
  DateTimeOffset? lastChecked;
  string lastError;
  int disposed;

  /// <summary>
  /// Creates a provider and starts keeping its databases current.
  /// </summary>
  /// <param name="options">The sources and update settings; they are copied.</param>
  /// <param name="precision">The most precise location to keep. Default is the country.</param>
  /// <param name="baseDirectory">The directory a relative <see cref="IpLocationOptions.Directory"/>
  /// is resolved against. Default is the current directory.</param>
  /// <param name="logger">Receives downloads, updates and failures. Default is none.</param>
  /// <exception cref="ArgumentException">The options are invalid.</exception>
  public IpLocationProvider(IpLocationOptions options, GeoPrecision precision = GeoPrecision.Country,
      string baseDirectory = null, ILogger logger = null)
  {
    ArgumentNullException.ThrowIfNull(options);
    if (precision == GeoPrecision.None || !Enum.IsDefined(precision))
      throw new ArgumentOutOfRangeException(nameof(precision), "Choose the country, region or city precision.");
    this.options = options.Clone();
    this.options.Validate();
    if (this.options.Sources.Count == 0)
      throw new ArgumentException("Add at least one IP location source.");
    this.precision = precision;
    this.logger = logger;
    paths = this.options.ResolvePaths(baseDirectory);
    ownsClient = this.options.HttpClient == null;
    client = this.options.HttpClient ?? CreateClient();
    loop = Task.Run(() => RunAsync(stopping.Token));
  }

  /// <summary>The database files, in source order.</summary>
  public IReadOnlyList<string> Paths => paths;

  /// <summary>The database in use, or null before one is available.</summary>
  public IpLocationDatabase Database => Volatile.Read(ref database);

  /// <summary>When the sources were last checked for a newer database, or null.</summary>
  public DateTimeOffset? LastChecked { get { lock (notBefore) return lastChecked; } }

  /// <summary>The last download or load failure, or null after a success.</summary>
  public string LastError { get { lock (notBefore) return lastError; } }

  /// <summary>
  /// Resolves the location of an IP address with the database in use. Returns null for an
  /// unknown or invalid address, and before a database is available.
  /// </summary>
  public GeoLocation Locate(string ipAddress) => Volatile.Read(ref database)?.Locate(ipAddress);

  /// <summary>
  /// Completes when a database is available, at once when one already is.
  /// </summary>
  public Task WaitForDatabaseAsync(CancellationToken cancellationToken = default) =>
      available.Task.WaitAsync(cancellationToken);

  /// <summary>
  /// Checks every source with a URL for a newer database now, downloads missing files, and loads
  /// what changed. Failures are logged and reported by <see cref="LastError"/>.
  /// </summary>
  /// <returns>Whether a new database is in use.</returns>
  public Task<bool> UpdateAsync(CancellationToken cancellationToken = default) => CheckAsync(due: false, cancellationToken);

  async Task RunAsync(CancellationToken stop)
  {
    try
    {
      await StartAsync(stop).ConfigureAwait(false);
      while (true)
      {
        var next = NextCheck();
        var wait = next == null ? MaxDelay : next.Value - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
          await Task.Delay(wait < MaxDelay ? wait : MaxDelay, stop).ConfigureAwait(false);
          continue;
        }
        await CheckAsync(due: true, stop).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
      logger?.LogError(new InvalidOperationException("IP location updates stopped.", exception));
    }
  }

  // Loads the files that exist; an engine opening with a large database does not wait for it.
  async Task StartAsync(CancellationToken stop)
  {
    await checking.WaitAsync(stop).ConfigureAwait(false);
    try
    {
      RemoveAbandonedDownloads();
      if (paths.All(File.Exists))
        Load();
    }
    finally
    {
      checking.Release();
    }
  }

  // When the next source is due, or null when none ever is.
  DateTimeOffset? NextCheck()
  {
    DateTimeOffset? next = null;
    for (var i = 0; i < paths.Length; i++)
      if (DueAt(i) is { } due && (next == null || due < next))
        next = due;
    return next;
  }

  // When a source is due: a missing file or one to download again at once, otherwise after the
  // update interval since its last check, and never before its earliest check. Null when it is
  // never due.
  DateTimeOffset? DueAt(int source)
  {
    if (options.Sources[source].Url == null) return null;
    var path = paths[source];
    lock (notBefore)
    {
      var missing = !File.Exists(path);
      DateTimeOffset? due;
      if (redownload || (missing && options.DownloadMissing))
        due = DateTimeOffset.MinValue;
      else if (!missing && options.UpdateInterval is { } interval)
        due = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) + interval;
      else
        return null;
      return notBefore.TryGetValue(path, out var earliest) && earliest > due ? earliest : due;
    }
  }

  async Task<bool> CheckAsync(bool due, CancellationToken cancellationToken)
  {
    ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
    var token = linked.Token;
    await checking.WaitAsync(token).ConfigureAwait(false);
    try
    {
      var now = DateTimeOffset.UtcNow;
      var changed = false;
      var failed = false;
      for (var i = 0; i < paths.Length; i++)
      {
        var source = options.Sources[i];
        if (source.Url == null) continue;
        if (due && !(DueAt(i) <= now)) continue;
        try
        {
          changed |= await DownloadAsync(source.Url, paths[i], force: redownload, token).ConfigureAwait(false);
          lock (notBefore) notBefore[paths[i]] = DateTimeOffset.UtcNow + (options.UpdateInterval ?? options.RetryInterval);
        }
        catch (Exception exception) when (!token.IsCancellationRequested || exception is not OperationCanceledException)
        {
          failed = true;
          var timedOut = exception is OperationCanceledException;
          Fail([paths[i]], timedOut ? $"Downloading {source.Url} timed out." : $"Downloading {source.Url} failed: {exception.Message}",
              timedOut ? null : exception);
        }
      }
      lock (notBefore)
      {
        lastChecked = DateTimeOffset.UtcNow;
        if (!failed) lastError = null;
      }
      // Unchanged files are loaded only while no database is in use.
      if ((!changed && Database != null) || !paths.All(File.Exists))
        return false;
      return Load();
    }
    finally
    {
      checking.Release();
    }
  }

  // Downloads a newer file, or the file in full when forced. Returns whether the file changed.
  async Task<bool> DownloadAsync(string url, string path, bool force, CancellationToken cancellationToken)
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(options.DownloadTimeout);
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    if (!force && File.Exists(path))
      request.Headers.IfModifiedSince = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
    // The server's clock dates the check, so a newer file is recognized whatever the local clock says.
    var checkedAt = response.Headers.Date ?? DateTimeOffset.UtcNow;
    if (response.StatusCode == HttpStatusCode.NotModified)
    {
      File.SetLastWriteTimeUtc(path, checkedAt.UtcDateTime);
      return false;
    }
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength > options.MaxDownloadBytes)
      throw new InvalidDataException($"The database is larger than {options.MaxDownloadBytes} bytes.");
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    var download = $"{path}.{Guid.NewGuid():N}.download";
    try
    {
      long size = 0;
      await using (var target = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
      await using (var content = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
      {
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await content.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
          size += read;
          if (size > options.MaxDownloadBytes)
            throw new InvalidDataException($"The database is larger than {options.MaxDownloadBytes} bytes.");
          await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
        }
      }
      // Only a file that loads replaces the current one.
      MmdbReader.Read(IpLocationFiles.Read(download), GeoPrecision.Country, new LocationTable());
      File.SetLastWriteTimeUtc(download, checkedAt.UtcDateTime);
      File.Move(download, path, overwrite: true);
      logger?.LogInfo($"Downloaded the IP location database {url} to {path}: {size} bytes.");
      return true;
    }
    finally
    {
      try { File.Delete(download); }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
  }

  bool Load()
  {
    try
    {
      var loaded = IpLocationDatabase.Load(paths, precision);
      Volatile.Write(ref database, loaded);
      lock (notBefore)
      {
        redownload = false;
        lastError = null;
      }
      available.TrySetResult();
      logger?.LogInfo($"Loaded the IP location database {string.Join(", ", paths)}: {loaded.DatabaseType}, " +
          $"built {loaded.BuildTime:yyyy-MM-dd}, {loaded.RangeCount} ranges, {loaded.LocationCount} locations.");
      return true;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
    {
      lock (notBefore) redownload = options.Sources.Any(source => source.Url != null);
      Fail(paths, $"Loading the IP location database {string.Join(", ", paths)} failed: {exception.Message}", exception);
      return false;
    }
  }

  void Fail(IEnumerable<string> failed, string message, Exception exception)
  {
    lock (notBefore)
    {
      lastError = message;
      foreach (var path in failed)
        notBefore[path] = DateTimeOffset.UtcNow + options.RetryInterval;
    }
    if (exception == null) logger?.LogWarning(message);
    else logger?.LogError(new IOException(message, exception));
  }

  void RemoveAbandonedDownloads()
  {
    foreach (var path in paths)
    {
      var directory = Path.GetDirectoryName(path);
      if (!Directory.Exists(directory)) continue;
      foreach (var download in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*.download"))
      {
        try
        {
          if (DateTime.UtcNow - File.GetLastWriteTimeUtc(download) > AbandonedDownloadAge)
            File.Delete(download);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
      }
    }
  }

  static HttpClient CreateClient()
  {
    // The download timeout bounds each request instead.
    var created = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    created.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZoneTree.Analytics", null));
    return created;
  }

  /// <summary>Stops updates. A download in progress is cancelled.</summary>
  public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

  /// <inheritdoc cref="Dispose"/>
  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref disposed, 1) != 0) return;
    stopping.Cancel();
    await loop.ConfigureAwait(false);
    // A check started by UpdateAsync ends before the client is disposed.
    await checking.WaitAsync().ConfigureAwait(false);
    if (ownsClient) client.Dispose();
    stopping.Dispose();
  }
}
