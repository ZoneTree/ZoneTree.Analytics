using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ZoneTree.Analytics.AspNetCore;

namespace ZoneTree.Analytics.UnitTests;

public sealed class AspNetCoreTests : DirectoryTest
{
  WebApplication app;

  HttpClient client;

  // Owned and disposed by the host.
  AnalyticsEngine Engine => app.Services.GetRequiredService<AnalyticsEngine>();

  [SetUp]
  public async Task SetUp()
  {
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseTestServer();
    builder.Services.AddZoneTreeAnalytics(
        options =>
        {
          options.DataDirectory = directory;
          TestEngine.UseTestDefaults(options);
          options.Web.Bots = BotTraffic.TrackSeparately;
          // Options are fixed once the engine exists: the timeout test needs its own value.
          if (TestContext.CurrentContext.Test.MethodName == nameof(ReportingApiReportsTimeouts))
            options.Queries.Timeout = TimeSpan.FromTicks(1);
          if (TestContext.CurrentContext.Test.MethodName == nameof(ReportingApiPreservesLegacyDefaultSiteIdentity))
            options.DefaultSite = " shop ";
        },
        web =>
        {
          web.VisitorIdResolver = context => context.Request.Headers["X-User"];
          web.AuthorizeSite = (_, site) => site != "forbidden.com";
        });
    app = builder.Build();
    app.UseZoneTreeAnalytics();
    app.MapZoneTreeAnalytics();
    app.MapZoneTreeAnalyticsApi();
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
  }

  static readonly DateRange Recent = DateRange.Last(TimeSpan.FromHours(1));

  double Count(string eventName) =>
      Engine.Query(new AnalyticsQuery { Event = eventName, Range = Recent, Metrics = { Metric.Events } }).Totals[Metric.Events];

  [Test]
  public void TheEngineIsRegisteredAsTracker() =>
      Assert.That(app.Services.GetRequiredService<IAnalyticsTracker>(), Is.SameAs(Engine));

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
    await Engine.FlushAsync();

    var pages = Engine.Query(new AnalyticsQuery
    {
      Range = Recent,
      GroupBy = { StandardDimensions.Page },
      OrderBy = { Order.ByDimension(StandardDimensions.Page) }
    }).Rows;
    Assert.Multiple(() =>
    {
      Assert.That(pages.Select(x => (x[StandardDimensions.Page], x[Metric.Events])), Is.EqualTo(new[] { ("/", 1.0), ("/docs", 1.0) }));
      Assert.That(Count(StandardEvents.Session), Is.EqualTo(1));
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

  [TestCase("%20forbidden.com%20")]
  [TestCase("forbidden.%00com")]
  public async Task ReportingApiAuthorizesTheCanonicalSite(string site)
  {
    Engine.Track(new AnalyticsEvent("pageview") { Site = "forbidden.com", Url = "/secret" });
    await Engine.FlushAsync();
    var response = await client.GetAsync("/_analytics/api/summary?site=" + site);
    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    var post = await client.PostAsync("/_analytics/api/query",
        new StringContent("{\"site\":\" forbidden.com \"}", Encoding.UTF8, "application/json"));
    Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
  }

  [TestCase(null)]
  [TestCase("%20%20")]
  public async Task ReportingApiAuthorizesTheEffectiveDefaultSite(string site)
  {
    var options = app.Services.GetRequiredService<AnalyticsWebOptions>();
    options.AuthorizeSite = (_, requestedSite) => requestedSite != Engine.DefaultSite;
    var response = await client.GetAsync("/_analytics/api/summary" + (site == null ? "" : "?site=" + site));
    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
  }

  [Test]
  public async Task ReportingApiPreservesLegacyDefaultSiteIdentity()
  {
    Engine.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/legacy" });
    await Engine.FlushAsync();
    string authorizedSite = null;
    var options = app.Services.GetRequiredService<AnalyticsWebOptions>();
    options.AuthorizeSite = (_, site) =>
    {
      authorizedSite = site;
      return site == " shop ";
    };
    var (status, report) = await GetJson("/_analytics/api/summary?range=1h");
    Assert.Multiple(() =>
    {
      Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(authorizedSite, Is.EqualTo(" shop "));
      Assert.That(report.GetProperty("summary").GetProperty("pageViews").GetInt32(), Is.EqualTo(1));
    });
  }

  [TestCase(" $session")]
  [TestCase("\t$session")]
  [TestCase("\0$session")]
  [TestCase("\0")]
  public async Task BeaconReturnsBadRequestForInvalidNormalizedNames(string name)
  {
    var json = JsonSerializer.Serialize(new { n = name });
    var response = await client.PostAsync("/_analytics/event", new StringContent(json, Encoding.UTF8, "text/plain"));
    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    Assert.That(Engine.GetStatistics().EventsAccepted, Is.Zero);
  }

  [Test]
  public async Task HashRoutingBeaconsKeepRoutesDistinctAndDiscardOrdinaryAnchors()
  {
    foreach (var (url, routing) in new[]
    {
      ("https://example.com/#/pricing?utm_source=newsletter&email=private", true),
      ("https://example.com/#!/about", true),
      ("https://example.com/#ordinary-anchor", false)
    })
    {
      var json = JsonSerializer.Serialize(new { n = "pageview", u = url, h = routing });
      var response = await client.PostAsync("/_analytics/event", new StringContent(json, Encoding.UTF8, "text/plain"));
      Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }
    await Engine.FlushAsync();
    var result = Engine.Query(new AnalyticsQuery
    {
      Range = Recent, GroupBy = { StandardDimensions.Page }, Metrics = { Metric.PageViews }
    });
    Assert.That(result.Rows.Select(x => x.Dimensions[0]), Is.EquivalentTo(new[] { "/pricing", "/about", "/" }));
    var script = await client.GetStringAsync("/_analytics/script.js");
    Assert.That(script, Does.Contain("payload.h = true"));
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
    await Engine.FlushAsync();

    var signup = Engine.Query(new AnalyticsQuery
    {
      Event = "signup",
      Source = QuerySource.RawEvents,
      Range = Recent,
      GroupBy = { "plan", "seats" },
      Metrics = { Metric.Visitors, Metric.Sum("value") }
    });
    var sources = Engine.Query(new AnalyticsQuery
    {
      Range = Recent,
      GroupBy = { StandardDimensions.Source },
      Metrics = { Metric.Sessions }
    });
    Assert.Multiple(() =>
    {
      Assert.That(accepted, Is.EqualTo(HttpStatusCode.Accepted));
      Assert.That(custom, Is.EqualTo(HttpStatusCode.Accepted));
      Assert.That(reserved, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(invalid, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(missingName, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(tooLarge, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
      Assert.That(Count(StandardEvents.PageView), Is.EqualTo(1));
      Assert.That(signup.Rows.Single().Dimensions, Is.EqualTo(new[] { "pro", "3" }));
      Assert.That(signup.Totals[Metric.Sum("value")], Is.EqualTo(49.5));
      Assert.That(signup.Totals[Metric.Visitors], Is.EqualTo(1));
      Assert.That(sources.Rows.Single().Dimensions[0], Is.EqualTo("Google"));
    });
  }

  async Task TrackVisitsAsync()
  {
    var now = DateTimeOffset.UtcNow;
    foreach (var (user, page, referrer) in new[]
    {
      ("u1", "/", "https://www.google.com/"),
      ("u1", "/pricing", null),
      ("u2", "/", null),
    })
    {
      Engine.Track(new AnalyticsEvent(StandardEvents.PageView)
      {
        VisitorId = user,
        Url = "https://example.com" + page,
        Referrer = referrer,
        Timestamp = now.AddMinutes(-10)
      });
    }
    Engine.Track(new AnalyticsEvent("signup") { VisitorId = "u1", Timestamp = now.AddMinutes(-9) });
    await Engine.FlushAsync();
  }

  async Task<(HttpStatusCode Status, JsonElement Json)> GetJson(string url)
  {
    var response = await client.GetAsync(url);
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return (response.StatusCode, document.RootElement.Clone());
  }

  async Task<(HttpStatusCode Status, JsonElement Json)> PostJson(string url, string body)
  {
    var response = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return (response.StatusCode, document.RootElement.Clone());
  }

  [Test]
  public async Task ReportingApiServesWebReports()
  {
    await TrackVisitsAsync();
    var (summaryStatus, summaryReport) = await GetJson("/_analytics/api/summary?range=today&compare=previous-period");
    var summary = summaryReport.GetProperty("summary");
    var (_, breakdown) = await GetJson("/_analytics/api/breakdown?dimension=source&range=1h");
    var (_, filtered) = await GetJson("/_analytics/api/summary?range=today&filter=source%3D%3DGoogle");
    var (_, segmented) = await GetJson("/_analytics/api/summary?range=today&segment=visitors%20with%20pageview%20where%20source%3D%3DGoogle");
    var (_, pages) = await GetJson("/_analytics/api/pages?range=today&limit=1");
    var (_, goals) = await GetJson("/_analytics/api/goals?range=today");
    var (_, series) = await GetJson("/_analytics/api/timeseries?range=today&interval=hour");
    var (_, realtime) = await GetJson("/_analytics/api/realtime?minutes=30");
    Assert.Multiple(() =>
    {
      Assert.That(summaryStatus, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(summary.GetProperty("visitors").GetInt32(), Is.EqualTo(2));
      Assert.That(summary.GetProperty("pageViews").GetInt32(), Is.EqualTo(3));
      Assert.That(summary.GetProperty("sessions").GetInt32(), Is.EqualTo(2));
      Assert.That(summary.GetProperty("previous").GetProperty("pageViews").GetInt32(), Is.EqualTo(0));
      Assert.That(summaryReport.GetProperty("plan").GetProperty("complete").GetBoolean(), Is.True);
      Assert.That(summaryReport.GetProperty("from").GetDateTimeOffset(), Is.LessThan(summaryReport.GetProperty("to").GetDateTimeOffset()));
      // Both sources have one visitor; Google wins the page view tie-breaker (2 vs 1).
      Assert.That(breakdown.GetProperty("rows")[0].GetProperty("value").GetString(), Is.EqualTo("Google"));
      Assert.That(breakdown.GetProperty("totalRows").GetInt32(), Is.EqualTo(2));
      Assert.That(filtered.GetProperty("summary").GetProperty("visitors").GetInt32(), Is.EqualTo(1));
      Assert.That(segmented.GetProperty("summary").GetProperty("visitors").GetInt32(), Is.EqualTo(1));
      Assert.That(pages.GetProperty("rows").GetArrayLength(), Is.EqualTo(1));
      Assert.That(pages.GetProperty("totalRows").GetInt32(), Is.EqualTo(2));
      Assert.That(goals.GetProperty("rows")[0].GetProperty("conversionRate").GetDouble(), Is.EqualTo(0.5));
      Assert.That(series.GetProperty("rows").GetArrayLength(), Is.EqualTo(24));
      Assert.That(series.GetProperty("totalRows").GetInt32(), Is.EqualTo(24));
      Assert.That(realtime.GetProperty("visitors").GetInt32(), Is.EqualTo(2));
    });
  }

  [Test]
  public async Task ReportingApiServesBotsSideBySide()
  {
    await TrackVisitsAsync();
    foreach (var userAgent in new[] { UserAgents.Googlebot, UserAgents.Googlebot, "curl/8.4.0" })
    {
      using var request = new HttpRequestMessage(HttpMethod.Get, "/");
      request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
      await client.SendAsync(request);
    }
    await Engine.FlushAsync();
    var summary = (await GetJson("/_analytics/api/summary?range=today")).Json.GetProperty("summary");
    var (_, pages) = await GetJson("/_analytics/api/pages?range=today");
    var (botsStatus, bots) = await GetJson("/_analytics/api/bots?range=today");
    var (_, segmented) = await GetJson("/_analytics/api/summary?range=today&filter=source%3D%3DGoogle");
    var rows = bots.GetProperty("rows");
    Assert.Multiple(() =>
    {
      Assert.That(summary.GetProperty("pageViews").GetInt32(), Is.EqualTo(3));
      Assert.That(summary.GetProperty("visitors").GetInt32(), Is.EqualTo(2));
      Assert.That(summary.GetProperty("botHits").GetInt32(), Is.EqualTo(3));
      Assert.That(pages.GetProperty("rows")[0].GetProperty("page").GetString(), Is.EqualTo("/"));
      Assert.That(pages.GetProperty("rows")[0].GetProperty("botHits").GetInt32(), Is.EqualTo(3));
      Assert.That(botsStatus, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(bots.GetProperty("totalRows").GetInt32(), Is.EqualTo(2));
      Assert.That(rows[0].GetProperty("name").GetString(), Is.EqualTo("Googlebot"));
      Assert.That(rows[0].GetProperty("category").GetString(), Is.EqualTo(BotCategories.Search));
      Assert.That(rows[0].GetProperty("hits").GetInt32(), Is.EqualTo(2));
      Assert.That(rows[0].GetProperty("share").GetDouble(), Is.EqualTo(2.0 / 3).Within(1e-9));
      Assert.That(rows[1].GetProperty("name").GetString(), Is.EqualTo("curl"));
      // Bots have no traffic source, so a source filter leaves bot hits out.
      Assert.That(segmented.GetProperty("summary").GetProperty("botHits").ValueKind, Is.EqualTo(JsonValueKind.Null));
    });
  }

  [Test]
  public async Task ReportingApiRunsQueriesAndFunnels()
  {
    await TrackVisitsAsync();
    var (status, result) = await PostJson("/_analytics/api/query", """
      {
        "range": "today",
        "groupBy": ["page"],
        "metrics": ["visitors", "pageviews", "conversion_rate:signup"],
        "orderBy": ["-pageviews", "page"],
        "filters": ["source!=Bing"]
      }
      """);
    var (funnelStatus, funnel) = await PostJson("/_analytics/api/funnel", """
      {
        "range": "today",
        "windowMinutes": 60,
        "steps": [
          { "name": "Home", "event": "pageview", "filters": ["page==/"] },
          { "name": "Signup", "event": "signup" }
        ]
      }
      """);
    var (segmentStatus, segmented) = await PostJson("/_analytics/api/query", """
      { "range": "today", "metrics": ["visitors"], "segments": [{ "event": "signup", "exclude": true }] }
      """);
    var (seriesStatus, series) = await PostJson("/_analytics/api/query", """
      {
        "range": "today",
        "interval": "30m",
        "fill": "none",
        "metrics": ["pageviews", "cumulative(pageviews)", "div(pageviews,visitors)"],
        "having": ["pageviews>=2"]
      }
      """);
    var (invalidStatus, _) = await PostJson("/_analytics/api/query", """{ "range": "today", "having": ["pageviews>>"] }""");
    var (topStatus, top) = await PostJson("/_analytics/api/query", """
      {
        "range": "today",
        "groupBy": ["page"],
        "metrics": ["pageviews", "where(pageviews,\"page==/pricing\")"],
        "orderBy": ["-pageviews"],
        "limitPerBucket": 1
      }
      """);
    var seriesRows = series.GetProperty("rows").EnumerateArray().ToList();
    var rows = result.GetProperty("rows");
    Assert.Multiple(() =>
    {
      Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(rows[0].GetProperty("dimensions").GetProperty("page").GetString(), Is.EqualTo("/"));
      Assert.That(rows[0].GetProperty("values").GetProperty("pageviews").GetDouble(), Is.EqualTo(2));
      // The signup has no URL, so it does not belong to a page; overall one of two visitors converted.
      Assert.That(rows[0].GetProperty("values").GetProperty("conversion_rate:signup").GetDouble(), Is.EqualTo(0));
      Assert.That(result.GetProperty("totals").GetProperty("values").GetProperty("conversion_rate:signup").GetDouble(), Is.EqualTo(0.5));
      Assert.That(result.GetProperty("totals").GetProperty("values").GetProperty("visitors").GetDouble(), Is.EqualTo(2));
      Assert.That(result.GetProperty("plan").GetProperty("complete").GetBoolean(), Is.True);
      Assert.That(funnelStatus, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(funnel.GetProperty("steps")[1].GetProperty("visitors").GetInt32(), Is.EqualTo(1));
      Assert.That(funnel.GetProperty("conversionRate").GetDouble(), Is.EqualTo(0.5));
      Assert.That(segmentStatus, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(segmented.GetProperty("totals").GetProperty("values").GetProperty("visitors").GetDouble(), Is.EqualTo(1));
      Assert.That(seriesStatus, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(series.GetProperty("interval").GetString(), Is.EqualTo("30m"));
      Assert.That(seriesRows, Is.Not.Empty);
      Assert.That(seriesRows.Select(x => x.GetProperty("values").GetProperty("pageviews").GetDouble()), Is.All.GreaterThanOrEqualTo(2));
      Assert.That(series.GetProperty("totals").GetProperty("values").GetProperty("div(pageviews,visitors)").GetDouble(), Is.EqualTo(1.5));
      Assert.That(invalidStatus, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(topStatus, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(top.GetProperty("rows").GetArrayLength(), Is.EqualTo(1));
      Assert.That(top.GetProperty("rows")[0].GetProperty("dimensions").GetProperty("page").GetString(), Is.EqualTo("/"));
      Assert.That(top.GetProperty("totals").GetProperty("values").GetProperty("where(pageviews,\"page==/pricing\")").GetDouble(), Is.EqualTo(1));
    });
  }

  [Test]
  public async Task ReportingApiReportsTimeouts()
  {
    var now = DateTimeOffset.UtcNow;
    for (var i = 0; i < 2000; ++i)
      Engine.Track(new AnalyticsEvent("e") { VisitorId = i.ToString(CultureInfo.InvariantCulture), Timestamp = now.AddSeconds(-i) });
    await Engine.FlushAsync();
    var (status, error) = await PostJson("/_analytics/api/query", """{ "event": "e", "range": "1h", "source": "RawEvents" }""");
    Assert.Multiple(() =>
    {
      Assert.That(status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
      Assert.That(error.GetProperty("error").GetString(), Does.Contain("Timeout"));
    });
  }

  [Test]
  public async Task ReportingApiValidatesRequests()
  {
    var (badRange, error) = await GetJson("/_analytics/api/summary?range=forever");
    var (badMetric, _) = await PostJson("/_analytics/api/query", """{ "metrics": ["median:x"] }""");
    var (badJson, _) = await PostJson("/_analytics/api/query", "{");
    var (badScope, scopeError) = await PostJson("/_analytics/api/query", """{ "groupBy": ["page"], "metrics": ["bounce_rate"] }""");
    var (missingDimension, _) = await GetJson("/_analytics/api/breakdown");
    var (forbidden, _) = await GetJson("/_analytics/api/summary?site=forbidden.com");
    Assert.Multiple(() =>
    {
      Assert.That(badRange, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(error.GetProperty("error").GetString(), Does.Contain("forever"));
      Assert.That(badMetric, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(badJson, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(badScope, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(scopeError.GetProperty("error").GetString(), Does.Contain("bounce_rate"));
      Assert.That(missingDimension, Is.EqualTo(HttpStatusCode.BadRequest));
      Assert.That(forbidden, Is.EqualTo(HttpStatusCode.Forbidden));
    });
  }
}
