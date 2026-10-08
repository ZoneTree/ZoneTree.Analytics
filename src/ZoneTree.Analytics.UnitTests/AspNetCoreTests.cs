using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ZoneTree.Analytics.AspNetCore;

namespace ZoneTree.Analytics.UnitTests;

public sealed class AspNetCoreTests
{
  string directory;

  WebApplication app;

  HttpClient client;

  // Owned and disposed by the host.
  AnalyticsEngine engine => app.Services.GetRequiredService<AnalyticsEngine>();

  [SetUp]
  public async Task SetUp()
  {
    directory = TestEngine.NewDirectory("aspnet");
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseTestServer();
    builder.Services.AddZoneTreeAnalytics(
        options =>
        {
          var test = TestEngine.Options(directory);
          options.DataDirectory = test.DataDirectory;
          options.Retention = test.Retention;
          options.Ingestion = test.Ingestion;
        },
        web => web.VisitorIdResolver = context => context.Request.Headers["X-User"]);
    app = builder.Build();
    app.UseZoneTreeAnalytics();
    app.MapZoneTreeAnalytics();
    app.MapGet("/", () => Results.Content("<html><body>home</body></html>", "text/html"));
    app.MapGet("/docs", () => Results.Content("<html><body>docs</body></html>", "text/html"));
    app.MapGet("/api/data", () => Results.Json(new { ok = true }));
    app.MapGet("/data.json", () => Results.Json(new { ok = true }));
    await app.StartAsync();
    client = app.GetTestClient();
    client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgents.ChromeWindows);
  }

  [TearDown]
  public async Task TearDown()
  {
    client?.Dispose();
    if (app != null)
      await app.DisposeAsync();
    TestEngine.Delete(directory);
  }

  AnalyticsRow Totals(string eventName)
  {
    var now = DateTime.UtcNow;
    return engine.Query(new AnalyticsQuery { Event = eventName }.Between(now.AddHours(-1), now.AddHours(1))).Totals;
  }

  [Test]
  public async Task MiddlewareTracksHtmlPageViewsOnly()
  {
    await client.GetAsync("/");
    var request = new HttpRequestMessage(HttpMethod.Get, "/docs");
    request.Headers.Referrer = new Uri("https://news.ycombinator.com/item?id=1");
    await client.SendAsync(request);
    await client.GetAsync("/api/data");
    await client.GetAsync("/data.json");
    await client.GetAsync("/missing");
    await client.PostAsync("/", new StringContent(""));
    await engine.FlushAsync();

    var pages = engine.Query(new AnalyticsQuery()
        .Between(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1))
        .GroupBy(StandardDimensions.Page)
        .Order(QueryOrder.ByDimension(StandardDimensions.Page))).Rows;
    Assert.Multiple(() =>
    {
      Assert.That(pages.Select(x => (x.Dimensions[0], x.Events)), Is.EqualTo(new[] { ("/", 1L), ("/docs", 1L) }));
      Assert.That(Totals(StandardEvents.Session).Events, Is.EqualTo(1));
    });
  }

  [Test]
  public async Task ScriptIsServed()
  {
    var response = await client.GetAsync("/_analytics/script.js");
    var script = await response.Content.ReadAsStringAsync();
    Assert.Multiple(() =>
    {
      Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("application/javascript"));
      Assert.That(script, Does.Contain("sendBeacon"));
    });
  }

  [Test]
  public async Task BeaconRecordsEventsWithDimensionsAndMetrics()
  {
    async Task<HttpStatusCode> Post(string json, string user = null)
    {
      var request = new HttpRequestMessage(HttpMethod.Post, "/_analytics/event")
      {
        Content = new StringContent(json, Encoding.UTF8, "text/plain")
      };
      if (user != null)
        request.Headers.Add("X-User", user);
      return (await client.SendAsync(request)).StatusCode;
    }

    var accepted = await Post("""{"n":"pageview","u":"https://example.com/pricing","r":"https://www.google.com/"}""", "u1");
    var custom = await Post("""{"n":"signup","u":"https://example.com/pricing","p":{"plan":"pro","seats":3},"m":{"value":49.5}}""", "u1");
    var reserved = await Post("""{"n":"$session"}""");
    var invalid = await Post("not json");
    var missingName = await Post("""{"u":"/"}""");
    var tooLarge = await Post("{\"n\":\"x\",\"p\":{\"a\":\"" + new string('x', 10_000) + "\"}}");
    await engine.FlushAsync();

    var signup = engine.Query(new AnalyticsQuery { Event = "signup", Source = QuerySource.RawEvents }
        .Between(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1))
        .GroupBy("plan", "seats"));
    var sources = engine.Query(new AnalyticsQuery { Event = StandardEvents.Session }
        .Between(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1))
        .GroupBy(StandardDimensions.Source));
    Assert.Multiple(() =>
    {
      Assert.That(accepted, Is.EqualTo(HttpStatusCode.Accepted));
      Assert.That(custom, Is.EqualTo(HttpStatusCode.Accepted));
      Assert.That(reserved, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(invalid, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(missingName, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(tooLarge, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
      Assert.That(Totals(StandardEvents.PageView).Events, Is.EqualTo(1));
      Assert.That(signup.Rows.Single().Dimensions, Is.EqualTo(new[] { "pro", "3" }));
      Assert.That(signup.Totals.Metric("value").Sum, Is.EqualTo(49.5));
      Assert.That(signup.Totals.Visitors, Is.EqualTo(1));
      Assert.That(sources.Rows.Single().Dimensions[0], Is.EqualTo("Google"));
    });
  }
}
