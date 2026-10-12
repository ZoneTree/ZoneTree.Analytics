using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZoneTree.Analytics.AspNetCore;

namespace ZoneTree.Analytics.UnitTests;

public sealed class AspNetCoreHardeningTests : DirectoryTest
{
  WebApplication app;

  HttpClient client;

  readonly ManualResetEventSlim ingestionGate = new();

  readonly ManualResetEventSlim ingestionEntered = new();

  TaskCompletionSource reportGate;

  TaskCompletionSource reportsEntered;

  int activeReports;

  AnalyticsEngine Engine => app.Services.GetRequiredService<AnalyticsEngine>();

  AnalyticsWebOptions WebOptions => app.Services.GetRequiredService<AnalyticsWebOptions>();

  [SetUp]
  public async Task SetUp()
  {
    ingestionGate.Reset();
    ingestionEntered.Reset();
    activeReports = 0;
    reportGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    reportsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseTestServer();
    builder.Services.AddZoneTreeAnalytics(options =>
    {
      options.DataDirectory = directory;
      TestEngine.UseTestDefaults(options);
      options.Ingestion.QueueCapacity = 1;
      if (TestContext.CurrentContext.Test.MethodName == nameof(BeaconRejectsAFullDropQueue))
        options.Ingestion.QueueFullBehavior = QueueFullBehavior.Drop;
      options.EventFilters.Add(EventFilter.Create(e => e.Name != "filtered"));
      options.Enrichers.Add(EventEnricher.Create(context =>
      {
        if (context.EventName == "block")
        {
          ingestionEntered.Set();
          if (!ingestionGate.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The test did not release ingestion.");
        }
      }));
    }, web =>
    {
      web.MaxConcurrentReportingRequests = 2;
      web.AuthorizeSite = (_, site) => site != "forbidden";
    });
    app = builder.Build();
    app.MapZoneTreeAnalytics();
    app.MapZoneTreeAnalyticsApi();
    app.MapGet("/probe", (RequestDelegate)(context =>
        ReportingApi.Run(context, HoldReportAsync, ReportingJsonContext.Default.RealtimeResponse)));
    await app.StartAsync();
    client = app.GetTestClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgents.ChromeWindows);
  }

  [TearDown]
  public async Task TearDown()
  {
    ingestionGate.Set();
    reportGate.TrySetResult();
    client?.Dispose();
    if (app != null)
      await app.DisposeAsync();
  }

  [OneTimeTearDown]
  public void DisposeGates()
  {
    ingestionGate.Dispose();
    ingestionEntered.Dispose();
  }

  async Task<RealtimeResponse> HoldReportAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken)
  {
    if (Interlocked.Increment(ref activeReports) == 2)
      reportsEntered.TrySetResult();
    try
    {
      await reportGate.Task.WaitAsync(cancellationToken);
      return new(1, 5);
    }
    finally
    {
      Interlocked.Decrement(ref activeReports);
    }
  }

  [TestCase("/_analytics/event", """{"n":"\uD800"}""")]
  [TestCase("/_analytics/event", """{"n":"x","p":{"\uDC00":"x"}}""")]
  [TestCase("/_analytics/event", """{"n":"x","unused":["\uD800"]}""")]
  [TestCase("/_analytics/api/query", """{"event":"\uD800"}""")]
  [TestCase("/_analytics/api/query", """{"metrics":["sum:\uDC00"]}""")]
  [TestCase("/_analytics/api/query", """{"\uD800":"unused"}""")]
  [TestCase("/_analytics/api/funnel", """{"steps":[{"event":"x","name":"\uD800"}]}""")]
  public async Task MalformedUnicodeReturnsBadRequest(string path, string body)
  {
    using var response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    if (path.Contains("/api/", StringComparison.Ordinal))
    {
      using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.That(document.RootElement.GetProperty("error").GetString(), Does.Contain("Unicode"));
    }
    Assert.That(Engine.GetStatistics().EventsAccepted, Is.Zero);
  }

  [Test]
  public async Task ValidSurrogatePairsRemainSupported()
  {
    using var beacon = await client.PostAsync("/_analytics/event",
        new StringContent("""{"n":"signup","p":{"label":"\uD83D\uDE00"}}""", Encoding.UTF8, "text/plain"));
    using var query = await client.PostAsync("/_analytics/api/query",
        new StringContent("""{"event":"signup","filters":["label==\uD83D\uDE00"]}""", Encoding.UTF8, "application/json"));
    Assert.Multiple(() =>
    {
      Assert.That(beacon.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
      Assert.That(query.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    });
  }

  [TestCase("FF")]
  [TestCase("C080")]
  [TestCase("EDA080")]
  [TestCase("F5808080")]
  public async Task InvalidUtf8ReturnsBadRequest(string hex)
  {
    foreach (var path in new[] { "/_analytics/event", "/_analytics/api/query" })
    {
      var name = path.EndsWith("/event", StringComparison.Ordinal) ? "n" : "event";
      byte[] bytes = [.. Encoding.UTF8.GetBytes("{\"" + name + "\":\""), .. Convert.FromHexString(hex), .. Encoding.UTF8.GetBytes("\"}")];
      using var response = await client.PostAsync(path, new ByteArrayContent(bytes));
      Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
  }

  [Test]
  public async Task Utf8ByteOrderMarksRemainSupportedWithinThePayloadBudget()
  {
    byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{}")];
    WebOptions.MaxReportingPayloadBytes = bytes.Length;
    using var response = await client.PostAsync("/_analytics/api/query", new UnknownLengthContent(bytes));
    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    WebOptions.MaxReportingPayloadBytes = bytes.Length - 1;
    using var rejected = await client.PostAsync("/_analytics/api/query", new UnknownLengthContent(bytes));
    Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
  }

  [TestCase("[]")]
  [TestCase("null")]
  [TestCase("true")]
  [TestCase("123")]
  [TestCase("\"query\"")]
  public async Task ReportingRejectsNonObjectBodies(string body)
  {
    foreach (var endpoint in new[] { "query", "funnel" })
    {
      using var response = await client.PostAsync("/_analytics/api/" + endpoint,
          new StringContent(body, Encoding.UTF8, "application/json"));
      using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(document.RootElement.GetProperty("error").GetString(), Does.Contain("object"));
    }
  }

  // Content-Length is absent for HTTP/1.1 chunked bodies and HTTP/2 streams. Also exercise
  // an incorrect length so checking only the header can never defeat the byte budget.
  [TestCase("/_analytics/event", false, false)]
  [TestCase("/_analytics/event", true, false)]
  [TestCase("/_analytics/event", true, true)]
  [TestCase("/_analytics/api/query", false, false)]
  [TestCase("/_analytics/api/query", true, false)]
  [TestCase("/_analytics/api/query", true, true)]
  [TestCase("/_analytics/api/funnel", true, false)]
  public async Task PayloadLimitsApplyWithAndWithoutContentLength(string path, bool streaming, bool incorrectLength)
  {
    WebOptions.MaxPayloadBytes = 64;
    WebOptions.MaxReportingPayloadBytes = 64;
    var bytes = Encoding.UTF8.GetBytes("{\"n\":\"x\",\"unused\":\"" + new string('x', 64) + "\"}");
    using HttpContent content = streaming ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
    if (incorrectLength)
      content.Headers.ContentLength = 1;
    using var response = await client.PostAsync(path, content);
    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    Assert.That(Engine.GetStatistics().EventsAccepted, Is.Zero);
    if (path.Contains("/api/", StringComparison.Ordinal))
    {
      using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      Assert.That(document.RootElement.GetProperty("error").GetString(), Does.Contain("limit"));
      Assert.That(response.Headers.CacheControl.NoStore, Is.True);
    }
  }

  [Test]
  public async Task ReportingPayloadLimitIsSeparateAndAcceptsAnExactSizeBody()
  {
    WebOptions.MaxPayloadBytes = 16;
    WebOptions.MaxReportingPayloadBytes = 64;
    var body = "{}" + new string(' ', 62);
    using var exact = await client.PostAsync("/_analytics/api/query", new UnknownLengthContent(Encoding.UTF8.GetBytes(body)));
    using var oversized = await client.PostAsync("/_analytics/api/query", new UnknownLengthContent(Encoding.UTF8.GetBytes(body + " ")));
    Assert.Multiple(() =>
    {
      Assert.That(exact.StatusCode, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(oversized.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    });
  }

  [Test]
  public async Task ReportingResponseLimitIsAtomicAndReleasesAdmission()
  {
    var dto = new RealtimeResponse(1, 5);
    var expected = JsonSerializer.SerializeToUtf8Bytes(dto, ReportingJsonContext.Default.RealtimeResponse);
    WebOptions.MaxReportingResponseBytes = expected.Length;
    using var body = new MemoryStream();
    var context = Context(body);
    await ReportingApi.Run(context, (_, _, _) => Task.FromResult(dto), ReportingJsonContext.Default.RealtimeResponse);
    Assert.That(body.ToArray(), Is.EqualTo(expected));
    WebOptions.MaxReportingResponseBytes = expected.Length - 1;
    // Repeated failures must release slots, and the error body must bypass the success
    // budget so even a very small configured limit can produce a useful response.
    for (var i = 0; i < 4; ++i)
    {
      using var responseBody = new MemoryStream();
      var rejected = Context(responseBody);
      await ReportingApi.Run(rejected, (_, _, _) => Task.FromResult(dto), ReportingJsonContext.Default.RealtimeResponse);
      using var document = JsonDocument.Parse(responseBody.ToArray());
      Assert.That(rejected.Response.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
      Assert.That(document.RootElement.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "error" }));
      Assert.That(document.RootElement.GetProperty("error").GetString(), Does.Contain("size limit"));
    }
  }

  DefaultHttpContext Context(Stream body)
  {
    var context = new DefaultHttpContext { RequestServices = app.Services };
    context.Request.Method = HttpMethods.Get;
    context.Response.Body = body;
    return context;
  }

  [Test]
  public async Task ReportingSaturationRejectsRequestsWithoutExecutingThem()
  {
    var first = client.GetAsync("/probe");
    var second = client.GetAsync("/probe");
    await reportsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var responses = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => client.GetAsync("/probe")));
    try
    {
      Assert.That(activeReports, Is.EqualTo(2));
      foreach (var response in responses)
      {
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(response.Headers.RetryAfter.Delta, Is.EqualTo(TimeSpan.FromSeconds(1)));
      }
    }
    finally
    {
      foreach (var response in responses)
        response.Dispose();
      reportGate.TrySetResult();
    }
    using var firstResponse = await first;
    using var secondResponse = await second;
    using var recovered = await client.GetAsync("/probe");
    Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    Assert.That(recovered.StatusCode, Is.EqualTo(HttpStatusCode.OK));
  }

  [Test]
  public async Task ReportingErrorsAndCancellationDoNotLeakAdmissionSlots()
  {
    for (var i = 0; i < 4; ++i)
    {
      using var badRequest = await client.PostAsync("/_analytics/api/query", new StringContent("[]"));
      using var forbidden = await client.GetAsync("/_analytics/api/summary?site=forbidden");
      Assert.That(badRequest.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
      using var body = new MemoryStream();
      using var cancellation = new CancellationTokenSource();
      var context = Context(body);
      context.RequestAborted = cancellation.Token;
      Assert.That(async () => await ReportingApi.Run<RealtimeResponse>(context, async (_, _, token) =>
      {
        cancellation.Cancel();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new(1, 5);
      }, ReportingJsonContext.Default.RealtimeResponse), Throws.InstanceOf<OperationCanceledException>());
      Assert.That(body.Length, Is.Zero);
    }
    using var recovered = await client.GetAsync("/_analytics/api/summary?range=1h");
    Assert.That(recovered.StatusCode, Is.EqualTo(HttpStatusCode.OK));
  }

  [Test]
  public async Task ReportingAdmissionCoversSlowResponseDeliveryAndReleasesOnDisconnect()
  {
    using var firstBody = new GatedWriteStream();
    using var secondBody = new GatedWriteStream();
    using var cancellation = new CancellationTokenSource();
    var firstContext = Context(firstBody);
    firstContext.RequestAborted = cancellation.Token;
    var first = ReportingApi.Run(firstContext, (_, _, _) => Task.FromResult(new RealtimeResponse(1, 5)),
        ReportingJsonContext.Default.RealtimeResponse);
    var second = ReportingApi.Run(Context(secondBody), (_, _, _) => Task.FromResult(new RealtimeResponse(1, 5)),
        ReportingJsonContext.Default.RealtimeResponse);
    await Task.WhenAll(firstBody.Entered.Task, secondBody.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10));
    try
    {
      using var busyBody = new MemoryStream();
      var busy = Context(busyBody);
      await ReportingApi.Run(busy, (_, _, _) => throw new AssertionException("The rejected report must not execute."),
          ReportingJsonContext.Default.RealtimeResponse);
      Assert.That(busy.Response.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
      cancellation.Cancel();
      Assert.That(async () => await first, Throws.InstanceOf<OperationCanceledException>());
      using var recoveredBody = new MemoryStream();
      var recovered = Context(recoveredBody);
      await ReportingApi.Run(recovered, (_, _, _) => Task.FromResult(new RealtimeResponse(1, 5)),
          ReportingJsonContext.Default.RealtimeResponse);
      Assert.That(recovered.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }
    finally
    {
      firstBody.Gate.TrySetResult();
      secondBody.Gate.TrySetResult();
    }
    await second;
  }

  [Test]
  public async Task ConcurrentQueriesStayWithinTheSerializedResponseBudget()
  {
    WebOptions.MaxReportingResponseBytes = 64 * 1024;
    for (var i = 0; i < 300; ++i)
      await Engine.TrackAsync(new AnalyticsEvent("pageview") { Url = "/page/" + i + new string('x', 80) });
    await Engine.FlushAsync();
    var timer = Stopwatch.StartNew();
    var responses = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => client.PostAsync("/_analytics/api/query",
        new StringContent("""{"range":"1h","groupBy":["page"],"metrics":["pageviews"],"limit":50}""", Encoding.UTF8, "application/json"))));
    var accepted = 0;
    var largest = 0;
    try
    {
      foreach (var response in responses)
      {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var document = JsonDocument.Parse(bytes);
        Assert.That(response.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable));
        if (response.StatusCode == HttpStatusCode.OK)
        {
          ++accepted;
          largest = Math.Max(largest, bytes.Length);
          Assert.That(bytes.Length, Is.LessThanOrEqualTo(WebOptions.MaxReportingResponseBytes));
          Assert.That(document.RootElement.GetProperty("rows").GetArrayLength(), Is.EqualTo(50));
          Assert.That(document.RootElement.GetProperty("totalRows").GetInt32(), Is.EqualTo(300));
        }
      }
      Assert.That(accepted, Is.GreaterThan(0));
      TestContext.Out.WriteLine($"64 concurrent queries: {accepted} successful, {64 - accepted} busy; largest response {largest} bytes; elapsed {timer.ElapsedMilliseconds} ms.");
    }
    finally
    {
      foreach (var response in responses)
        response.Dispose();
    }
  }

  void FillIngestionQueue()
  {
    Assert.That(Engine.Track(new AnalyticsEvent("block")), Is.True);
    Assert.That(ingestionEntered.Wait(TimeSpan.FromSeconds(10)), Is.True);
    Assert.That(Engine.Track(new AnalyticsEvent("queued")), Is.True);
  }

  [Test]
  public async Task BeaconRejectsAFullDropQueue()
  {
    FillIngestionQueue();
    using var response = await client.PostAsync("/_analytics/event", new StringContent("""{"n":"signup"}"""));
    Assert.Multiple(() =>
    {
      Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
      Assert.That(response.Headers.RetryAfter.Delta, Is.EqualTo(TimeSpan.FromSeconds(1)));
      Assert.That(Engine.GetStatistics().EventsAccepted, Is.EqualTo(2));
      Assert.That(Engine.GetStatistics().EventsDropped, Is.EqualTo(1));
    });
  }

  [Test]
  public async Task BeaconWaitsAsynchronouslyAndAcceptsAfterQueueSpaceIsAvailable()
  {
    FillIngestionQueue();
    var request = client.PostAsync("/_analytics/event", new StringContent("""{"n":"signup"}"""));
    Assert.That(request.IsCompleted, Is.False);
    // Another endpoint must remain available while this request waits for ingestion.
    using var report = await client.GetAsync("/_analytics/api/summary?range=1h");
    Assert.That(report.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    ingestionGate.Set();
    using var response = await request.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    await Engine.FlushAsync();
    Assert.That(Engine.Query(new AnalyticsQuery { Event = "signup", Range = DateRange.Last(TimeSpan.FromHours(1)), Metrics = { Metric.Events } })
        .Totals[Metric.Events], Is.EqualTo(1));
  }

  [Test]
  public async Task BeaconQueueWaitingHonorsRequestCancellation()
  {
    FillIngestionQueue();
    using var cancellation = new CancellationTokenSource();
    using var requestBody = new MemoryStream(Encoding.UTF8.GetBytes("""{"n":"signup"}"""));
    var context = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = cancellation.Token };
    context.Request.Method = HttpMethods.Post;
    context.Request.Body = requestBody;
    var request = TrackerEndpoints.ReceiveEvent(context);
    Assert.That(request.IsCompleted, Is.False, "ReceiveEvent must return while the queue is full");
    cancellation.Cancel();
    Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
    ingestionGate.Set();
    await Engine.FlushAsync();
    Assert.That(Engine.GetStatistics().EventsAccepted, Is.EqualTo(2));
    Assert.That(Engine.Query(new AnalyticsQuery { Event = "signup", Range = DateRange.Last(TimeSpan.FromHours(1)), Metrics = { Metric.Events } })
        .Totals[Metric.Events], Is.Zero);
  }

  [Test]
  public async Task PageViewMiddlewareQueueWaitingIsAsynchronousAndCancellable()
  {
    FillIngestionQueue();
    using var body = new MemoryStream();
    using var cancellation = new CancellationTokenSource();
    var context = Context(body);
    context.Request.Path = "/page";
    context.Request.Headers.UserAgent = UserAgents.ChromeWindows;
    context.RequestAborted = cancellation.Token;
    var middleware = new PageViewTrackingMiddleware(c =>
    {
      c.Response.ContentType = "text/html";
      return Task.CompletedTask;
    }, Engine, WebOptions);
    var request = middleware.InvokeAsync(context);
    Assert.That(request.IsCompleted, Is.False);
    cancellation.Cancel();
    await request.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.Multiple(() =>
    {
      Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
      Assert.That(Engine.GetStatistics().EventsAccepted, Is.EqualTo(2));
      Assert.That(Engine.GetStatistics().Errors, Is.Zero);
    });
  }

  [Test]
  public async Task FilteredBeaconsReturnNoContent()
  {
    using var response = await client.PostAsync("/_analytics/event", new StringContent("""{"n":"filtered"}"""));
    Assert.Multiple(() =>
    {
      Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
      Assert.That(response.Headers.RetryAfter, Is.Null);
      Assert.That(Engine.GetStatistics().EventsAccepted, Is.Zero);
      Assert.That(Engine.GetStatistics().EventsFiltered, Is.EqualTo(1));
    });
  }

  [Test]
  public async Task HostDisposalReleasesWaitingBeaconsAndPersistsAcceptedEvents()
  {
    FillIngestionQueue();
    using var requestBody = new MemoryStream(Encoding.UTF8.GetBytes("""{"n":"signup"}"""));
    var context = new DefaultHttpContext { RequestServices = app.Services };
    context.Request.Method = HttpMethods.Post;
    context.Request.Body = requestBody;
    var request = TrackerEndpoints.ReceiveEvent(context);
    Assert.That(request.IsCompleted, Is.False);
    var shutdown = app.DisposeAsync().AsTask();
    await request.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
    ingestionGate.Set();
    await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
    app = null;
    using var reopened = TestEngine.Create(directory);
    var query = new AnalyticsQuery { Event = "queued", Range = DateRange.Last(TimeSpan.FromHours(1)), Metrics = { Metric.Events } };
    Assert.That(reopened.Query(query).Totals[Metric.Events], Is.EqualTo(1));
    query.Event = "block";
    Assert.That(reopened.Query(query).Totals[Metric.Events], Is.EqualTo(1));
    query.Event = "signup";
    Assert.That(reopened.Query(query).Totals[Metric.Events], Is.Zero);
  }

  sealed class UnknownLengthContent(byte[] bytes) : HttpContent
  {
    protected override bool TryComputeLength(out long length)
    {
      length = 0;
      return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => stream.WriteAsync(bytes).AsTask();
  }

  sealed class GatedWriteStream : Stream
  {
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
      Entered.TrySetResult();
      await Gate.Task.WaitAsync(cancellationToken);
    }
  }
}

public sealed class AnalyticsWebOptionValidationTests
{
  [TestCase(nameof(AnalyticsWebOptions.MaxPayloadBytes), 0)]
  [TestCase(nameof(AnalyticsWebOptions.MaxPayloadBytes), -1)]
  [TestCase(nameof(AnalyticsWebOptions.MaxReportingPayloadBytes), 0)]
  [TestCase(nameof(AnalyticsWebOptions.MaxReportingPayloadBytes), -1)]
  [TestCase(nameof(AnalyticsWebOptions.MaxConcurrentReportingRequests), 0)]
  [TestCase(nameof(AnalyticsWebOptions.MaxConcurrentReportingRequests), -1)]
  [TestCase(nameof(AnalyticsWebOptions.MaxReportingResponseBytes), 0)]
  [TestCase(nameof(AnalyticsWebOptions.MaxReportingResponseBytes), -1)]
  public void RegistrationRejectsInvalidLimits(string property, int value)
  {
    var services = new ServiceCollection();
    Assert.That(() => services.AddZoneTreeAnalytics(configureWeb: options =>
    {
      switch (property)
      {
        case nameof(AnalyticsWebOptions.MaxPayloadBytes): options.MaxPayloadBytes = value; break;
        case nameof(AnalyticsWebOptions.MaxReportingPayloadBytes): options.MaxReportingPayloadBytes = value; break;
        case nameof(AnalyticsWebOptions.MaxConcurrentReportingRequests): options.MaxConcurrentReportingRequests = value; break;
        case nameof(AnalyticsWebOptions.MaxReportingResponseBytes): options.MaxReportingResponseBytes = value; break;
      }
    }), Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  [Test]
  public void SerializedBufferCapacityCannotGrowPastItsLimit()
  {
    using var buffer = new ReportingResponseBuffer(257);
    buffer.Write(new byte[256]);
    buffer.WriteByte(0);
    Assert.That(buffer.Length, Is.EqualTo(257));
    Assert.That(buffer.Capacity, Is.EqualTo(257));
    Assert.That(() => buffer.WriteByte(0), Throws.TypeOf<ReportingResponseTooLargeException>());
    Assert.That(buffer.Length, Is.EqualTo(257));
    Assert.That(buffer.Capacity, Is.EqualTo(257));
  }
}
