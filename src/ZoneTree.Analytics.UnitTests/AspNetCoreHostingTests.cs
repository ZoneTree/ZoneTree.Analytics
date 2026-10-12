using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZoneTree.Analytics.AspNetCore;

namespace ZoneTree.Analytics.UnitTests;

public sealed class AspNetCoreHostingTests : DirectoryTest
{
  async Task<WebApplication> StartHost(bool kestrel)
  {
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    if (kestrel)
      builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
    else
      builder.WebHost.UseTestServer();
    builder.Services.AddAuthentication("Test")
        .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
    builder.Services.AddAuthorization();
    builder.Services.AddRateLimiter(options =>
    {
      options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
      options.AddFixedWindowLimiter("beacons", limiter =>
      {
        limiter.PermitLimit = 1;
        limiter.QueueLimit = 0;
        limiter.Window = TimeSpan.FromMinutes(1);
      });
    });
    builder.Services.AddZoneTreeAnalytics(options =>
    {
      options.DataDirectory = directory;
      TestEngine.UseTestDefaults(options);
    }, web =>
    {
      web.MaxPayloadBytes = 128;
      web.MaxConcurrentReportingRequests = 2;
      web.MaxReportingResponseBytes = 64 * 1024;
      web.AuthorizeSite = (_, site) => site != "forbidden";
    });
    var app = builder.Build();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();
    var beacons = app.MapZoneTreeAnalytics();
    if (!kestrel)
      beacons.RequireRateLimiting("beacons");
    app.MapZoneTreeAnalyticsApi().RequireAuthorization();
    await app.StartAsync();
    return app;
  }

  [Test]
  public async Task HostPoliciesProtectReportingAndRateLimitBeacons()
  {
    await using var app = await StartHost(kestrel: false);
    using var client = app.GetTestClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgents.ChromeWindows);
    using var anonymous = await client.GetAsync("/_analytics/api/summary?range=1h");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "allowed");
    using var authorized = await client.GetAsync("/_analytics/api/summary?range=1h");
    using var forbidden = await client.GetAsync("/_analytics/api/summary?range=1h&site=forbidden");
    using var firstBeacon = await client.PostAsync("/_analytics/event", new StringContent("""{"n":"signup"}"""));
    using var limitedBeacon = await client.PostAsync("/_analytics/event", new StringContent("""{"n":"signup"}"""));
    Assert.Multiple(() =>
    {
      Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
      Assert.That(authorized.StatusCode, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
      Assert.That(firstBeacon.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
      Assert.That(limitedBeacon.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
      Assert.That(app.Services.GetRequiredService<AnalyticsEngine>().GetStatistics().EventsAccepted, Is.EqualTo(1));
    });
  }

  [Test]
  public async Task KestrelHandlesConcurrentQueriesChunkedLimitsAndShutdownPersistence()
  {
    var app = await StartHost(kestrel: true);
    try
    {
      using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
      client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgents.ChromeWindows);
      client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "allowed");
      using var oversized = await client.PostAsync("/_analytics/event",
          new ChunkedContent(Encoding.UTF8.GetBytes("{\"n\":\"signup\",\"unused\":\"" + new string('x', 128) + "\"}")));
      using var malformed = await client.PostAsync("/_analytics/api/query", new StringContent("""{"event":"\uD800"}"""));
      Assert.That(oversized.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
      Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
      var engine = app.Services.GetRequiredService<AnalyticsEngine>();
      for (var i = 0; i < 300; ++i)
        await engine.TrackAsync(new AnalyticsEvent("pageview") { Url = "/page/" + i + new string('x', 80) });
      await engine.FlushAsync();
      var queries = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => client.PostAsync("/_analytics/api/query",
          new StringContent("""{"range":"1h","groupBy":["page"],"metrics":["pageviews"],"limit":50}""", Encoding.UTF8, "application/json"))));
      var successful = 0;
      try
      {
        foreach (var response in queries)
        {
          var bytes = await response.Content.ReadAsByteArrayAsync();
          using var document = JsonDocument.Parse(bytes);
          Assert.That(response.StatusCode, Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable));
          if (response.StatusCode == HttpStatusCode.OK)
          {
            ++successful;
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(64 * 1024));
            Assert.That(document.RootElement.GetProperty("rows").GetArrayLength(), Is.EqualTo(50));
            Assert.That(document.RootElement.GetProperty("totalRows").GetInt32(), Is.EqualTo(300));
          }
        }
        Assert.That(successful, Is.GreaterThan(0));
        TestContext.Out.WriteLine($"Kestrel: 64 concurrent queries, {successful} successful, {64 - successful} busy; every success fits 64 KiB.");
      }
      finally
      {
        foreach (var response in queries)
          response.Dispose();
      }
      var beacons = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => client.PostAsync("/_analytics/event",
          new StringContent("""{"n":"signup"}""", Encoding.UTF8, "text/plain"))));
      try
      {
        Assert.That(beacons.Select(response => response.StatusCode), Is.All.EqualTo(HttpStatusCode.Accepted));
      }
      finally
      {
        foreach (var response in beacons)
          response.Dispose();
      }
      // No explicit flush after the beacons: host disposal must persist their aggregates.
      using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      await app.StopAsync(shutdown.Token);
      await app.DisposeAsync();
      app = null;
      using var reopened = TestEngine.Create(directory);
      var result = reopened.Query(new AnalyticsQuery
      {
        Event = "signup", Range = DateRange.Last(TimeSpan.FromHours(1)), Source = QuerySource.Rollups, Metrics = { Metric.Events }
      });
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(32));
    }
    finally
    {
      if (app != null)
        await app.DisposeAsync();
    }
  }

  sealed class TestAuthenticationHandler(
      IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
      : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
  {
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
      if (Request.Headers.Authorization != "Bearer allowed")
        return Task.FromResult(AuthenticateResult.NoResult());
      var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user")], "Test"));
      return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
    }
  }

  sealed class ChunkedContent(byte[] bytes) : HttpContent
  {
    protected override bool TryComputeLength(out long length)
    {
      length = 0;
      return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => stream.WriteAsync(bytes).AsTask();
  }
}
