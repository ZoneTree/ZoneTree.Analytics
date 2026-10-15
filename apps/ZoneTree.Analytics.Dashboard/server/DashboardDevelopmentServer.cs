using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// PureStack owns source watching, rebuilding and live reload. This host keeps
// the development UI and the real analytics API on one origin.
sealed class DashboardDevelopmentServer(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    ILogger<DashboardDevelopmentServer> logger) : IHostedService, IDisposable
{
  readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
  {
    Timeout = Timeout.InfiniteTimeSpan,
  };
  Process? process;
  Uri? origin;

  public async Task StartAsync(CancellationToken cancellationToken)
  {
    var source = Path.GetFullPath(configuration["Dashboard:SourcePath"]
        ?? Path.Combine(environment.ContentRootPath, "../ui"));
    var cli = ResolvePureStackCli(source);
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    origin = new Uri($"http://127.0.0.1:{port}");
    var start = new ProcessStartInfo(configuration["Dashboard:NodePath"] ?? "node")
    {
      WorkingDirectory = source,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (var argument in new[] { cli, "serve", "--content", "./content", "--host", "127.0.0.1", "--port", port.ToString(), "--full-render" })
      start.ArgumentList.Add(argument);
    process = new Process { StartInfo = start, EnableRaisingEvents = true };
    process.OutputDataReceived += (_, line) => { if (!string.IsNullOrWhiteSpace(line.Data)) logger.LogInformation("PureStack: {Line}", line.Data); };
    process.ErrorDataReceived += (_, line) => { if (!string.IsNullOrWhiteSpace(line.Data)) logger.LogWarning("PureStack: {Line}", line.Data); };
    process.Exited += (_, _) =>
    {
      if (!lifetime.ApplicationStopping.IsCancellationRequested)
      {
        logger.LogError("PureStack exited. Stopping the development host.");
        lifetime.StopApplication();
      }
    };
    try
    {
      process.Start();
      process.BeginOutputReadLine();
      process.BeginErrorReadLine();
      using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      startup.CancelAfter(TimeSpan.FromSeconds(60));
      while (true)
      {
        startup.Token.ThrowIfCancellationRequested();
        if (process.HasExited)
          throw new InvalidOperationException($"PureStack exited with code {process.ExitCode}. Check its output above.");
        try
        {
          using var response = await client.GetAsync(origin, HttpCompletionOption.ResponseHeadersRead, startup.Token);
          if (response.IsSuccessStatusCode) break;
        }
        catch (HttpRequestException) { }
        await Task.Delay(200, startup.Token);
      }
      logger.LogInformation("Dashboard live reload enabled. Watching {Source}. Production publish is not required in Development.", source);
    }
    catch
    {
      StopProcess();
      throw;
    }
  }

  static string ResolvePureStackCli(string source)
  {
    // Follow Node's ancestor lookup so hoisted workspace dependencies are found.
    for (var directory = new DirectoryInfo(source); directory != null; directory = directory.Parent)
    {
      var cli = Path.Combine(directory.FullName, "node_modules/purestack/dist/cli.mjs");
      if (File.Exists(cli)) return cli;
    }
    throw new InvalidOperationException("Install dashboard dependencies first: run yarn install from the repository root.");
  }

  public async Task ForwardAsync(HttpContext context)
  {
    // Reporting and tracker routes are handled by ASP.NET Core, including misses.
    if (context.Request.Path.StartsWithSegments("/_analytics"))
    {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return;
    }
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
      context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
      return;
    }
    var path = context.Request.PathBase.Add(context.Request.Path).ToUriComponent() + context.Request.QueryString;
    // Live-reload requests remain open while the browser is connected. Cancel
    // them as soon as shutdown starts, before Kestrel waits for requests to drain.
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
        context.RequestAborted, lifetime.ApplicationStopping);
    var requestCancellation = cancellation.Token;
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
        new Uri(origin!.GetLeftPart(UriPartial.Authority) + path));
    try
    {
      using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation);
      context.Response.StatusCode = (int)response.StatusCode;
      foreach (var header in response.Headers.Concat(response.Content.Headers))
      {
        if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
        context.Response.Headers[header.Key] = header.Value.ToArray();
      }
      context.Response.Headers.CacheControl = "no-store";
      if (HttpMethods.IsHead(context.Request.Method)) return;
      await using var body = await response.Content.ReadAsStreamAsync(requestCancellation);
      var buffer = new byte[16 * 1024];
      int read;
      // Flush each chunk so PureStack's event stream reaches browsers immediately.
      while ((read = await body.ReadAsync(buffer, requestCancellation)) > 0)
      {
        await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), requestCancellation);
        await context.Response.Body.FlushAsync(requestCancellation);
      }
    }
    catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
    {
      context.Abort();
    }
    catch (HttpRequestException error)
    {
      logger.LogError(error, "Cannot reach the PureStack development server.");
      if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
      else context.Abort();
    }
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    StopProcess();
    return Task.CompletedTask;
  }

  void StopProcess()
  {
    if (process is null) return;
    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    catch (InvalidOperationException) { }
  }

  public void Dispose()
  {
    StopProcess();
    process?.Dispose();
    client.Dispose();
  }
}
