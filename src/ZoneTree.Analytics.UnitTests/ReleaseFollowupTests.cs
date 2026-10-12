using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class ReleaseFollowupTests : DirectoryTest
{
  static readonly DateTime T0 = DateTime.UtcNow.Date.AddDays(-2).AddHours(10);

  [TestCase(false, false)]
  [TestCase(false, true)]
  [TestCase(true, false)]
  [TestCase(true, true)]
  public void InvalidTimerIntervalsFailBeforeOwningTheDirectory(bool queryTimeout, bool justAboveMaximum)
  {
    var interval = justAboveMaximum
        ? TimeSpan.FromMilliseconds(uint.MaxValue - 1).Add(TimeSpan.FromTicks(1))
        : TimeSpan.FromDays(60);
    Assert.Throws<ArgumentException>(() => TestEngine.Create(directory, o =>
    {
      if (queryTimeout)
        o.Queries.Timeout = interval;
      else
        o.Ingestion.FlushInterval = interval;
    }).Dispose());
    Assert.That(Directory.Exists(directory), Is.False);
    using var reopened = TestEngine.Create(directory);
    Assert.That(reopened.Track(Page("/valid", 0)), Is.True);
    reopened.Flush();
  }

  [Test]
  public void MaximumTimerIntervalsAreAccepted()
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Ingestion.FlushInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
      o.Queries.Timeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    });
    engine.Track(Page("/valid", 0));
    engine.Flush();
    Assert.That(engine.Query(new AnalyticsQuery { Range = Day(T0.Date) }).Totals[Metric.Events], Is.EqualTo(1));
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void FailedEnricherDiscardsAllChangesBeforeTheNextEnricher(QuerySource source)
  {
    var logger = new RecordingLogger();
    var names = new[] { "page", "hostname", "country", "variant", "section", "partial" };
    var ownChangesVisible = false;
    string[] laterValues = null;
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.Logger = logger;
      o.Sessions.Dimensions.Add("variant");
      o.Rollups.Definitions.Add(new RollupDefinition(names));
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension("section", "original");
        context.SetDimension("variant", "A");
        context.SetDimension("country", "DE");
      }));
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension("page", "/partial");
        context.SetDimension("page", "/partial-again");
        context.SetDimension("hostname", null);
        context.SetDimension("country", "US");
        context.SetDimension("variant", null);
        context.SetDimension("section", null);
        context.SetDimension("partial", "new");
        ownChangesVisible = context.GetDimension("page") == "/partial-again" &&
            context.GetDimension("section") == null && context.GetDimension("variant") == null;
        throw new InvalidOperationException("enricher failure after mutation");
      }));
      o.Enrichers.Add(EventEnricher.Create(context => laterValues = names.Select(context.GetDimension).ToArray()));
    });
    engine.Track(Page("/original", 0));
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Range = Day(T0.Date), Source = source, Metrics = { Metric.PageViews }
    };
    query.GroupBy.AddRange(names);
    var result = engine.Query(query);
    var expected = new[] { "/original", "example.com", "DE", "A", "original", null };
    Assert.Multiple(() =>
    {
      Assert.That(ownChangesVisible, Is.True);
      Assert.That(laterValues, Is.EqualTo(expected));
      Assert.That(result.Rows.Single().Dimensions, Is.EqualTo(expected));
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(1));
      Assert.That(logger.Problems, Has.Count.EqualTo(1));
      Assert.That(logger.Problems, Has.One.Contains("enricher failure after mutation"));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void FailedEnricherPreservesThePageOnHumanAndBotEvents(bool bot)
  {
    var logger = new RecordingLogger();
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.Logger = logger;
      o.Web.Bots = BotTraffic.TrackSeparately;
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension(StandardDimensions.Page, "/partial");
        throw new InvalidOperationException("page mutation failure");
      }));
    });
    var hit = Page("/original", 0);
    hit.UserAgent = bot ? UserAgents.Googlebot : UserAgents.ChromeWindows;
    engine.Track(hit);
    engine.Flush();
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups })
    {
      var result = engine.Query(new AnalyticsQuery
      {
        Range = Day(T0.Date), Source = source,
        GroupBy = { StandardDimensions.Page }, Metrics = { bot ? Metric.BotHits : Metric.PageViews }
      });
      Assert.That(result.Rows.Single()[StandardDimensions.Page], Is.EqualTo("/original"));
    }
    Assert.That(engine.GetStatistics().Errors, Is.EqualTo(1));
    Assert.That(logger.Problems, Has.Count.EqualTo(1));
    Assert.That(logger.Problems, Has.One.Contains("page mutation failure"));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void PageEligibilityIsAppliedBeforeCountingAndPaging(bool raw)
  {
    using var engine = TestEngine.Create(directory, o => ConfigureSource(o, raw));
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Timestamp = Utc(T0), VisitorId = "missing" });
    engine.Track(new AnalyticsEvent(StandardEvents.PageView) { Timestamp = Utc(T0.AddMinutes(1)), VisitorId = "missing" });
    engine.Track(Page("/a", 2));
    var second = Page("/b", 3);
    second.VisitorId = "other";
    engine.Track(second);
    engine.Flush();

    var request = new WebReportRequest { Range = Day(T0.Date), Limit = 1 };
    var first = engine.Web.GetPages(request);
    request.Offset = 1;
    var next = engine.Web.GetPages(request);
    request.Offset = 2;
    var empty = engine.Web.GetPages(request);
    Assert.Multiple(() =>
    {
      Assert.That(first.Rows.Single().Page, Is.EqualTo("/a"));
      Assert.That(next.Rows.Single().Page, Is.EqualTo("/b"));
      Assert.That(empty.Rows, Is.Empty);
      Assert.That(new[] { first.TotalRows, next.TotalRows, empty.TotalRows }, Is.All.EqualTo(2));
      Assert.That(first.Plan.UsedRawEvents, Is.EqualTo(raw));
    });
  }

  [TestCase(false)]
  [TestCase(true)]
  public void ExitEligibilityPreservesSessionFlowAndPreviousRows(bool raw)
  {
    using var engine = TestEngine.Create(directory, o => ConfigureSource(o, raw));
    foreach (var day in new[] { -1, 0 })
    {
      var first = Page("/first", 0);
      first.Timestamp = Utc(T0.AddDays(day));
      var last = Page("/last", 1);
      last.Timestamp = Utc(T0.AddDays(day).AddMinutes(1));
      engine.Track(first);
      engine.Track(last);
    }
    engine.Flush();
    var request = new WebReportRequest { Range = Day(T0.Date), Compare = ComparisonPeriod.PreviousPeriod, Limit = 1 };
    var exits = engine.Web.GetExitPages(request);
    var pages = engine.Web.GetPages(request);
    request.Offset = 1;
    var empty = engine.Web.GetExitPages(request);
    Assert.Multiple(() =>
    {
      Assert.That(exits.TotalRows, Is.EqualTo(1));
      Assert.That(exits.Rows.Single().Page, Is.EqualTo("/last"));
      Assert.That(exits.Rows.Single().Exits, Is.EqualTo(1));
      Assert.That(exits.Rows.Single().Previous.Exits, Is.EqualTo(1));
      Assert.That(pages.TotalRows, Is.EqualTo(2));
      Assert.That(pages.Rows.Single().Page, Is.EqualTo("/first"));
      Assert.That(pages.Rows.Single().Entrances, Is.EqualTo(1));
      Assert.That(pages.Rows.Single().Previous.Entrances, Is.EqualTo(1));
      Assert.That(empty.Rows, Is.Empty);
      Assert.That(empty.TotalRows, Is.EqualTo(1));
      Assert.That(exits.Plan.UsedRawEvents, Is.EqualTo(raw));
    });
  }

  static void ConfigureSource(AnalyticsOptions options, bool raw)
  {
    if (raw)
      options.Rollups.Definitions.Clear();
    else
      options.Storage.StoreRawEvents = false;
  }

  static AnalyticsEvent Page(string page, int minute) => new(StandardEvents.PageView)
  {
    Url = "https://example.com" + page, VisitorId = "visitor", Timestamp = Utc(T0.AddMinutes(minute))
  };
}
