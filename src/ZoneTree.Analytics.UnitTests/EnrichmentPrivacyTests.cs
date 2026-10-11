using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class EnrichmentPrivacyTests : DirectoryTest
{
  static readonly DateTime T0 = DateTime.UtcNow.Date.AddDays(-2).AddHours(10);

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void EnrichedDimensionsRespectAllowAndDenyLists(QuerySource source)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Web.Bots = BotTraffic.TrackSeparately;
      o.Privacy.AllowedDimensions = new HashSet<string> { "section", "email" };
      o.Privacy.DeniedDimensions.Add("email");
      foreach (var dimension in new[] { "section", "email", "unlisted" })
        o.Rollups.Definitions.Add(new RollupDefinition(dimension));
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension(" email ", "private@example.com");
        context.SetDimension("unlisted", "private");
        context.SetDimension("section", "docs");
      }));
      // A later enricher must not be able to reintroduce a denied dimension.
      o.Enrichers.Add(EventEnricher.Create(context => context.SetDimension("email", "still-private@example.com")));
    });
    engine.Track(Hit(false).WithDimension("email", "tracked-private@example.com"));
    engine.Flush();

    Assert.Multiple(() =>
    {
      AssertDimension(engine, source, false, "email", null);
      AssertDimension(engine, source, false, "unlisted", null);
      AssertDimension(engine, source, false, "section", "docs");
      Assert.That(engine.GetStatistics().Errors, Is.Zero);
    });
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void DenyListAppliesWithoutAnAllowList(QuerySource source)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Web.Bots = BotTraffic.TrackSeparately;
      o.Privacy.DeniedDimensions.Add("email");
      o.Privacy.MaxCustomDimensions = 1;
      o.Rollups.Definitions.Add(new RollupDefinition("email"));
      o.Rollups.Definitions.Add(new RollupDefinition("section"));
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension("email", "private@example.com");
        context.SetDimension("section", "docs");
      }));
    });
    engine.Track(Hit(false));
    engine.Flush();

    Assert.Multiple(() =>
    {
      AssertDimension(engine, source, false, "email", null);
      // Discarded dimensions do not consume the limit.
      AssertDimension(engine, source, false, "section", "docs");
    });
  }

  [TestCase(QuerySource.RawEvents, 0)]
  [TestCase(QuerySource.Rollups, 0)]
  [TestCase(QuerySource.RawEvents, 1)]
  [TestCase(QuerySource.Rollups, 1)]
  public void TrackedAndEnrichedDimensionsShareTheFinalLimit(QuerySource source, int limit)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Web.Bots = BotTraffic.TrackSeparately;
      o.Privacy.MaxCustomDimensions = limit;
      foreach (var dimension in new[] { "retained", "overflow" })
        o.Rollups.Definitions.Add(new RollupDefinition(dimension));
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension("retained", "updated");
        context.SetDimension("retained", "final");
        context.SetDimension("overflow", "private");
        context.SetDimension(StandardDimensions.Page, "/normalized");
        context.SetDimension(StandardDimensions.Hostname, "normalized.example");
      }));
    });
    engine.Track(Hit(false).WithDimension("retained", "tracked"));
    engine.Flush();

    Assert.Multiple(() =>
    {
      AssertDimension(engine, source, false, "retained", limit == 0 ? null : "final");
      AssertDimension(engine, source, false, "overflow", null);
      AssertDimension(engine, source, false, StandardDimensions.Page, "/normalized");
      AssertDimension(engine, source, false, StandardDimensions.Hostname, "normalized.example");
      Assert.That(engine.GetStatistics().Errors, Is.Zero);
    });
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void RemovedDimensionsFreeCapacityBeforeTheFinalLimit(QuerySource source)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Privacy.MaxCustomDimensions = 2;
      foreach (var dimension in new[] { "retained", "removed", "added", "overflow" })
        o.Rollups.Definitions.Add(new RollupDefinition(dimension));
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension("added", "kept");
        context.SetDimension("overflow", "discarded");
      }));
      o.Enrichers.Add(EventEnricher.Create(context => context.SetDimension("removed", null)));
    });
    engine.Track(Hit(false).WithDimension("retained", "tracked").WithDimension("removed", "original"));
    engine.Flush();

    Assert.Multiple(() =>
    {
      AssertDimension(engine, source, false, "retained", "tracked");
      AssertDimension(engine, source, false, "removed", null);
      AssertDimension(engine, source, false, "added", "kept");
      AssertDimension(engine, source, false, "overflow", null);
    });
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  public void SessionDimensionsDoNotConsumeTheEventLimit(QuerySource source)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Privacy.MaxCustomDimensions = 0;
      o.Privacy.AllowedDimensions = new HashSet<string> { "variant" };
      o.Sessions.Dimensions.Add("variant");
      o.Rollups.Definitions.Add(new RollupDefinition("variant"));
      o.Rollups.Definitions.Add(new RollupDefinition("overflow"));
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension(StandardDimensions.Country, "Germany");
        context.SetDimension("variant", "A");
        context.SetDimension("overflow", "private");
      }));
    });
    engine.Track(Hit(false));
    engine.Flush();

    Assert.Multiple(() =>
    {
      AssertDimension(engine, source, false, "variant", "A");
      AssertDimension(engine, source, false, StandardDimensions.Country, "Germany");
      AssertDimension(engine, source, false, "overflow", null);
    });
  }

  [TestCase(0)]
  [TestCase(1)]
  public void BotEventsRespectPrivacyAndKeepEngineDimensions(int limit)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Web.Bots = BotTraffic.TrackSeparately;
      o.Privacy.MaxCustomDimensions = limit;
      o.Privacy.AllowedDimensions = new HashSet<string> { "email", "section", "overflow" };
      o.Privacy.DeniedDimensions.Add("email");
      o.Enrichers.Add(EventEnricher.Create(context =>
      {
        context.SetDimension("email", "private@example.com");
        context.SetDimension("unlisted", "private");
        context.SetDimension("section", "docs");
        context.SetDimension("overflow", "private");
        context.SetDimension(StandardDimensions.Page, "/normalized");
      }));
    });
    engine.Track(Hit(true));
    engine.Flush();

    // Bot reports cannot group by custom dimensions; inspect the stored record itself.
    var events = new List<RawEvent>();
    engine.RawEventStore.Scan("default", T0.Date.Ticks, T0.Date.AddDays(1).Ticks, 1,
        () => events, (list, ticks, value) =>
        {
          var raw = new RawEvent();
          Assert.That(RawEventCodec.TryDecode(value, ticks, null, default, raw), Is.True);
          list.Add(raw);
        }, CancellationToken.None);
    var expected = new Dictionary<string, string>
    {
      [StandardDimensions.Page] = "/normalized",
      [StandardDimensions.Hostname] = "example.com",
      [StandardDimensions.Bot] = "Googlebot",
      [StandardDimensions.BotCategory] = BotCategories.Search,
      [StandardDimensions.Event] = StandardEvents.PageView
    };
    if (limit > 0)
      expected["section"] = "docs";
    Assert.Multiple(() =>
    {
      Assert.That(events, Has.Count.EqualTo(1));
      Assert.That(events.Single().Dimensions, Is.EquivalentTo(expected));
      foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups })
        AssertDimension(engine, source, true, StandardDimensions.Page, "/normalized");
      Assert.That(engine.GetStatistics().Errors, Is.Zero);
    });
  }

  static AnalyticsEvent Hit(bool bot) => new(StandardEvents.PageView)
  {
    Url = "https://example.com/original",
    UserAgent = bot ? UserAgents.Googlebot : UserAgents.ChromeWindows,
    IpAddress = "10.0.0.1",
    Timestamp = Utc(T0)
  };

  static void AssertDimension(AnalyticsEngine engine, QuerySource source, bool bot, string dimension, string expected)
  {
    var metric = bot ? Metric.BotHits : Metric.PageViews;
    var result = engine.Query(new AnalyticsQuery
    {
      Range = Day(T0.Date),
      Source = source,
      GroupBy = { dimension },
      Metrics = { metric }
    });
    Assert.That(result.Rows, Has.Count.EqualTo(1), dimension);
    var row = result.Rows.Single();
    Assert.That(row[dimension], Is.EqualTo(expected), dimension);
    Assert.That(row[metric], Is.EqualTo(1), dimension);
    Assert.That(result.Plan.UsedRawEvents, Is.EqualTo(source == QuerySource.RawEvents));
    Assert.That(result.Plan.UsedRollups, Is.EqualTo(source == QuerySource.Rollups));
  }
}
