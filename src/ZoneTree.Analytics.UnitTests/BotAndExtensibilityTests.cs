using System.Text.RegularExpressions;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Bot traffic and the extension points.
/// </summary>
/// <remarks>
/// Humans: 10.0.0.1 (Chrome, DE) views / and /pricing; 10.0.0.2 (Safari, US) views /.
/// Bots: Googlebot fetches /, /pricing and sends a signup from /; GPTBot fetches /;
/// curl fetches /robots-only. Totals: 3 page views, 2 visitors, 5 bot hits.
/// </remarks>
public sealed class BotAndExtensibilityTests
{
  static readonly DateTime T0 = DateTime.UtcNow.Date.AddDays(-3).AddHours(10);

  static readonly QuerySource[] Sources = [QuerySource.Rollups, QuerySource.RawEvents];

  const string GptBot =
      "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)";

  string directory;

  AnalyticsEngine engine;

  [SetUp]
  public void SetUp()
  {
    directory = TestEngine.NewDirectory("bots");
    engine = TestEngine.Create(directory, o => o.Web.Bots = BotTraffic.TrackSeparately);
  }

  [TearDown]
  public void TearDown()
  {
    engine?.Dispose();
    TestEngine.Delete(directory);
  }

  void Recreate(Action<AnalyticsOptions> configure)
  {
    engine.Dispose();
    TestEngine.Delete(directory);
    engine = TestEngine.Create(directory, configure);
  }

  static DateRange Range => DateRange.Between(Utc(T0.Date), Utc(T0.Date.AddDays(1)));

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  static AnalyticsEvent Hit(string path, string ip, string userAgent, int minute, string name = StandardEvents.PageView) =>
      new(name)
      {
        Url = "https://mysite.com" + path,
        IpAddress = ip,
        UserAgent = userAgent,
        Timestamp = Utc(T0.AddMinutes(minute))
      };

  void TrackTraffic()
  {
    engine.Track(Hit("/", "10.0.0.1", UserAgents.ChromeWindows, 0));
    engine.Track(Hit("/pricing", "10.0.0.1", UserAgents.ChromeWindows, 1));
    engine.Track(Hit("/", "10.0.0.2", UserAgents.SafariIPhone, 2));
    engine.Track(Hit("/", "66.249.0.1", UserAgents.Googlebot, 3));
    engine.Track(Hit("/pricing", "66.249.0.1", UserAgents.Googlebot, 4));
    engine.Track(Hit("/", "20.0.0.1", GptBot, 5));
    engine.Track(Hit("/robots-only", "1.2.3.4", "curl/8.4.0", 6));
    engine.Track(Hit("/", "66.249.0.1", UserAgents.Googlebot, 7, "signup"));
    engine.Flush();
  }

  AnalyticsResult Query(Action<AnalyticsQuery> configure)
  {
    var query = new AnalyticsQuery { Range = Range };
    configure(query);
    return engine.Query(query);
  }

  static WebReportRequest Request() => new() { Range = Range };

  #region Bot traffic

  [TestCaseSource(nameof(Sources))]
  public void BotHitsAreReportedSideBySideWithPageViews(QuerySource source)
  {
    TrackTraffic();
    var result = Query(q =>
    {
      q.Source = source;
      q.GroupBy.Add(StandardDimensions.Page);
      q.Metrics.AddRange([Metric.PageViews, Metric.Visitors, Metric.BotHits]);
    });
    var rows = result.Rows.ToDictionary(x => x[StandardDimensions.Page], x => (x[Metric.PageViews], x[Metric.BotHits]));
    Assert.Multiple(() =>
    {
      Assert.That(rows["/"], Is.EqualTo((2.0, 3.0)));
      Assert.That(rows["/pricing"], Is.EqualTo((1.0, 1.0)));
      Assert.That(rows["/robots-only"], Is.EqualTo((0.0, 1.0)));
      Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(3));
      Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(2));
      Assert.That(result.Totals[Metric.BotHits], Is.EqualTo(5));
      Assert.That(engine.GetStatistics().BotsTracked, Is.EqualTo(5));
      Assert.That(engine.GetStatistics().BotsExcluded, Is.EqualTo(0));
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(0));
    });
  }

  [TestCaseSource(nameof(Sources))]
  public void BotEventsCarryBotNameCategoryAndOriginalEvent(QuerySource source)
  {
    TrackTraffic();
    var byBot = Query(q =>
    {
      q.Source = source;
      q.Event = StandardEvents.Bot;
      q.GroupBy.AddRange([StandardDimensions.BotCategory, StandardDimensions.Bot]);
      q.Metrics.Add(Metric.Events);
    }).Rows.ToDictionary(x => (x[StandardDimensions.BotCategory], x[StandardDimensions.Bot]), x => x[Metric.Events]);
    var byEvent = Query(q =>
    {
      q.Source = source;
      q.Event = StandardEvents.Bot;
      q.GroupBy.Add(StandardDimensions.Event);
      q.Metrics.Add(Metric.Events);
    }).Rows.ToDictionary(x => x[StandardDimensions.Event], x => x[Metric.Events]);
    Assert.Multiple(() =>
    {
      Assert.That(byBot, Is.EquivalentTo(new Dictionary<(string, string), double>
      {
        [(BotCategories.Search, "Googlebot")] = 3,
        [(BotCategories.AI, "GPTBot")] = 1,
        [(BotCategories.Scripts, "curl")] = 1,
      }));
      Assert.That(byEvent, Is.EquivalentTo(new Dictionary<string, double> { ["pageview"] = 4, ["signup"] = 1 }));
    });
  }

  [Test]
  public async Task WebReportsShowBotHits()
  {
    TrackTraffic();
    var summary = await engine.Web.GetSummaryAsync(Request());
    var pages = await engine.Web.GetPagesAsync(Request());
    var series = await engine.Web.GetTimeseriesAsync(Request(), TimeInterval.Day);
    var bots = await engine.Web.GetBotsAsync(Request());
    var pricingRequest = Request();
    pricingRequest.Filters.Add(Filter.Equal(StandardDimensions.Page, "/pricing"));
    var pricingBots = await engine.Web.GetBotsAsync(pricingRequest);
    var aiRequest = Request();
    aiRequest.Filters.Add(Filter.Equal(StandardDimensions.BotCategory, BotCategories.AI));
    var aiBots = await engine.Web.GetBotsAsync(aiRequest);
    var goals = await engine.Web.GetGoalsAsync(Request());
    Assert.Multiple(() =>
    {
      Assert.That(summary.PageViews, Is.EqualTo(3));
      Assert.That(summary.Visitors, Is.EqualTo(2));
      Assert.That(summary.BotHits, Is.EqualTo(5));
      Assert.That(pages.Rows.Select(x => (x.Page, x.PageViews, x.BotHits)),
          Is.EqualTo(new[] { ("/", 2L, (long?)3), ("/pricing", 1L, 1) }));
      Assert.That(series.Single().Summary.BotHits, Is.EqualTo(5));
      Assert.That(bots.TotalRows, Is.EqualTo(3));
      Assert.That(bots.Rows[0], Is.EqualTo(new BotStats("Googlebot", BotCategories.Search, 3, 0.6)));
      Assert.That(bots.Rows.Skip(1).Select(x => x.Name), Is.EquivalentTo(new[] { "GPTBot", "curl" }));
      Assert.That(pricingBots.Rows.Select(x => (x.Name, x.Hits)), Is.EqualTo(new[] { ("Googlebot", 1L) }));
      Assert.That(aiBots.Rows.Select(x => x.Name), Is.EqualTo(new[] { "GPTBot" }));
      // A bot's custom event is a bot hit, not a goal.
      Assert.That(goals.Rows, Is.Empty);
    });
  }

  [Test]
  public async Task BotHitsAreNullWhenTheRequestCannotApplyToBots()
  {
    TrackTraffic();
    var byCountry = Request();
    byCountry.Filters.Add(Filter.Equal(StandardDimensions.Country, "DE"));
    var segmented = Request();
    segmented.Segments.Add(Segment.VisitorsWhoViewed("/pricing"));
    var byPage = Request();
    byPage.Filters.Add(Filter.Equal(StandardDimensions.Page, "/"));
    Assert.Multiple(async () =>
    {
      Assert.That((await engine.Web.GetSummaryAsync(byCountry)).BotHits, Is.Null);
      Assert.That((await engine.Web.GetSummaryAsync(segmented)).BotHits, Is.Null);
      Assert.That((await engine.Web.GetSummaryAsync(byPage)).BotHits, Is.EqualTo(3));
    });
  }

  [Test]
  public async Task BotsAreExcludedByDefault()
  {
    Recreate(_ => { });
    TrackTraffic();
    var summary = await engine.Web.GetSummaryAsync(Request());
    var bots = await engine.Web.GetBotsAsync(Request());
    var statistics = engine.GetStatistics();
    Assert.Multiple(() =>
    {
      Assert.That(summary.PageViews, Is.EqualTo(3));
      Assert.That(summary.BotHits, Is.Null);
      Assert.That(bots.Rows, Is.Empty);
      Assert.That(Query(q => q.Metrics.Add(Metric.BotHits)).Totals[Metric.BotHits], Is.EqualTo(0));
      Assert.That(statistics.BotsExcluded, Is.EqualTo(5));
      Assert.That(statistics.BotsTracked, Is.EqualTo(0));
    });
  }

  [Test]
  public void BotMetricsRejectDimensionsBotsDoNotHave()
  {
    TrackTraffic();
    var byCountry = Assert.Throws<AnalyticsQueryException>(() => Query(q =>
    {
      q.GroupBy.Add(StandardDimensions.Country);
      q.Metrics.Add(Metric.BotHits);
    }));
    var byBrowser = Assert.Throws<AnalyticsQueryException>(() => Query(q =>
    {
      q.Event = StandardEvents.Bot;
      q.Filters.Add(Filter.Equal(StandardDimensions.Browser, "Chrome"));
    }));
    var segmented = Assert.Throws<AnalyticsQueryException>(() => Query(q =>
    {
      q.Segments.Add(Segment.VisitorsWhoViewed("/"));
      q.Metrics.Add(Metric.BotHits);
    }));
    Assert.Multiple(() =>
    {
      Assert.That(byCountry.Message, Does.Contain("bot_hits").And.Contain("country"));
      Assert.That(byBrowser.Message, Does.Contain("browser"));
      Assert.That(segmented.Message, Does.Contain("segments"));
    });
  }

  [Test]
  public void BotHitsMetricParses()
  {
    Assert.That(Metric.Parse("bot_hits"), Is.EqualTo(Metric.BotHits));
  }

  #endregion

  #region Bot detectors

  [Test]
  public async Task CustomBotDetectorsRunBeforeTheBuiltInOne()
  {
    var contexts = new List<EventContext>();
    Recreate(o =>
    {
      o.Web.Bots = BotTraffic.TrackSeparately;
      o.Web.BotDetectors.Insert(0, BotDetector.Create(context =>
      {
        lock (contexts)
          contexts.Add(context);
        return context.IpAddress == "10.0.0.2" || context.TrackedDimensions.ContainsKey("synthetic")
            ? new BotInfo("Internal checker", BotCategories.Monitoring)
            : null;
      }));
    });
    TrackTraffic();
    engine.Track(new AnalyticsEvent(StandardEvents.PageView)
    {
      Url = "/",
      IpAddress = "10.0.0.9",
      UserAgent = UserAgents.ChromeWindows,
      Timestamp = Utc(T0)
    }.WithDimension("synthetic", "1"));
    engine.Flush();
    var summary = await engine.Web.GetSummaryAsync(Request());
    var bots = await engine.Web.GetBotsAsync(Request());
    var chrome = contexts.First(x => x.IpAddress == "10.0.0.1");
    Assert.Multiple(() =>
    {
      Assert.That(summary.Visitors, Is.EqualTo(1));
      Assert.That(summary.PageViews, Is.EqualTo(2));
      Assert.That(bots.Rows[0], Is.EqualTo(new BotStats("Googlebot", BotCategories.Search, 3, 3.0 / 7)));
      Assert.That(bots.Rows.Single(x => x.Name == "Internal checker").Hits, Is.EqualTo(2));
      Assert.That(chrome.UserAgentInfo.Browser, Is.EqualTo("Chrome"));
      Assert.That(chrome.Site, Is.EqualTo("default"));
      Assert.That(chrome.EventName, Is.EqualTo(StandardEvents.PageView));
    });
  }

  [Test]
  public async Task WithoutDetectorsBotsCountAsHumans()
  {
    Recreate(o => o.Web.BotDetectors.Clear());
    TrackTraffic();
    var summary = await engine.Web.GetSummaryAsync(Request());
    Assert.Multiple(() =>
    {
      Assert.That(summary.PageViews, Is.EqualTo(7));
      Assert.That(summary.Visitors, Is.EqualTo(5));
      Assert.That(engine.GetStatistics().BotsExcluded, Is.EqualTo(0));
    });
  }

  #endregion

  #region Enrichers, parsers, filters

  [Test]
  public void EnrichersChangePagesAndSessionDimensions()
  {
    Recreate(o =>
    {
      o.Enrichers.Add(EventEnricher.NormalizePage(path => Regex.Replace(path, @"/\d+", "/:id")));
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension("section", context.GetDimension(StandardDimensions.Page).Split('/')[1]);
        if (context.GetDimension(StandardDimensions.Country) == "DE")
          context.SetDimension(StandardDimensions.Country, "Germany");
        context.SetDimension(StandardDimensions.Region, null);
      }));
    });
    engine.Track(Hit("/users/123", "10.0.0.1", UserAgents.ChromeWindows, 0));
    engine.Track(Hit("/users/456", "10.0.0.1", UserAgents.ChromeWindows, 1));
    engine.Track(Hit("/docs/a", "10.0.0.2", UserAgents.SafariIPhone, 2));
    engine.Flush();
    Dictionary<string, double> By(string dimension, Metric metric) => Query(q =>
    {
      q.GroupBy.Add(dimension);
      q.Metrics.Add(metric);
    }).Rows.ToDictionary(x => x[dimension] ?? "(none)", x => x[metric]);
    Assert.Multiple(() =>
    {
      Assert.That(By(StandardDimensions.Page, Metric.PageViews),
          Is.EquivalentTo(new Dictionary<string, double> { ["/users/:id"] = 2, ["/docs/a"] = 1 }));
      Assert.That(By(StandardDimensions.EntryPage, Metric.Sessions),
          Is.EquivalentTo(new Dictionary<string, double> { ["/users/:id"] = 1, ["/docs/a"] = 1 }));
      Assert.That(By("section", Metric.PageViews),
          Is.EquivalentTo(new Dictionary<string, double> { ["users"] = 2, ["docs"] = 1 }));
      Assert.That(By(StandardDimensions.Country, Metric.Sessions),
          Is.EquivalentTo(new Dictionary<string, double> { ["Germany"] = 1, ["US"] = 1 }));
      Assert.That(By(StandardDimensions.Region, Metric.Sessions),
          Is.EquivalentTo(new Dictionary<string, double> { ["(none)"] = 2 }));
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(0));
    });
  }

  [Test]
  public void EnrichersSeeBotsAndCannotOverwriteEngineDimensions()
  {
    var rejected = new List<string>();
    var bots = new List<string>();
    Recreate(o =>
    {
      o.Web.Bots = BotTraffic.TrackSeparately;
      o.Enrichers.Add(EventEnricher.NormalizePage(path => path.ToUpperInvariant()));
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        lock (bots)
        {
          if (context.Bot != null)
            bots.Add(context.Bot.Name);
        }
        foreach (var name in new[] { StandardDimensions.EntryPage, StandardDimensions.Bot, "a,b" })
        {
          try
          {
            context.SetDimension(name, "x");
          }
          catch (ArgumentException)
          {
            lock (rejected)
              rejected.Add(name);
          }
        }
      }));
    });
    engine.Track(Hit("/a", "10.0.0.1", UserAgents.ChromeWindows, 0));
    engine.Track(Hit("/a", "66.249.0.1", UserAgents.Googlebot, 1));
    engine.Flush();
    var pages = Query(q =>
    {
      q.GroupBy.Add(StandardDimensions.Page);
      q.Metrics.AddRange([Metric.PageViews, Metric.BotHits]);
    }).Rows.Single();
    Assert.Multiple(() =>
    {
      Assert.That(bots, Is.EqualTo(new[] { "Googlebot" }));
      // Humans may set "bot"; bot events may not.
      Assert.That(rejected, Is.EquivalentTo(new[]
      {
        StandardDimensions.EntryPage, "a,b",
        StandardDimensions.EntryPage, StandardDimensions.Bot, "a,b"
      }));
      Assert.That(pages[StandardDimensions.Page], Is.EqualTo("/A"));
      Assert.That((pages[Metric.PageViews], pages[Metric.BotHits]), Is.EqualTo((1.0, 1.0)));
    });
  }

  [Test]
  public async Task CustomUserAgentParserFeedsDimensions()
  {
    Recreate(o => o.Web.UserAgentParser = UserAgentParser.Create(
        ua => new UserAgentInfo("Custom", "1", "CustomOS", "2", "Desktop")));
    engine.Track(Hit("/", "10.0.0.1", UserAgents.SafariIPhone, 0));
    engine.Flush();
    var browsers = await engine.Web.GetBreakdownAsync(Request(), StandardDimensions.Browser);
    var devices = await engine.Web.GetBreakdownAsync(Request(), StandardDimensions.Device);
    Assert.Multiple(() =>
    {
      Assert.That(browsers.Rows.Single().Value, Is.EqualTo("Custom"));
      Assert.That(devices.Rows.Single().Value, Is.EqualTo("Desktop"));
    });
  }

  [Test]
  public async Task FailingExtensionsAreLoggedAndIgnored()
  {
    static T Fail<T>() => throw new InvalidOperationException("extension failure");
    Recreate(o =>
    {
      o.EventFilters.Add(EventFilter.Create(_ => Fail<bool>()));
      o.Enrichers.Add(EventEnricher.Create(_ => Fail<bool>()));
      o.Web.UserAgentParser = UserAgentParser.Create(_ => Fail<UserAgentInfo>());
      o.Web.BotDetectors.Insert(0, BotDetector.Create(_ => Fail<BotInfo>()));
      o.Web.GeoLocator = GeoLocator.Create(_ => Fail<GeoLocation>());
    });
    engine.Track(Hit("/", "10.0.0.1", UserAgents.ChromeWindows, 0));
    engine.Track(Hit("/b", "10.0.0.1", UserAgents.ChromeWindows, 1));
    engine.Track(Hit("/", "66.249.0.1", UserAgents.Googlebot, 2));
    engine.Flush();
    var summary = await engine.Web.GetSummaryAsync(Request());
    var statistics = engine.GetStatistics();
    Assert.Multiple(() =>
    {
      Assert.That(summary.PageViews, Is.EqualTo(2));
      Assert.That(summary.Visitors, Is.EqualTo(1));
      // The built-in detector still recognizes Googlebot after the failing custom one.
      Assert.That(statistics.BotsExcluded, Is.EqualTo(1));
      // Per event: filter, parser and detector; per human event: enricher; per session: geolocator.
      Assert.That(statistics.Errors, Is.GreaterThanOrEqualTo(3 * 3 + 2 + 1));
    });
  }

  [Test]
  public void FactoriesRejectNull()
  {
    Assert.Multiple(() =>
    {
      Assert.Throws<ArgumentNullException>(() => EventFilter.Create(null));
      Assert.Throws<ArgumentNullException>(() => EventEnricher.Create(null));
      Assert.Throws<ArgumentNullException>(() => EventEnricher.NormalizePage(null));
      Assert.Throws<ArgumentNullException>(() => BotDetector.Create(null));
      Assert.Throws<ArgumentNullException>(() => UserAgentParser.Create(null));
      Assert.Throws<ArgumentNullException>(() => GeoLocator.Create(null));
    });
  }

  #endregion
}
