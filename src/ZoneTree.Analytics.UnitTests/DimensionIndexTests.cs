using ZoneTree.Analytics.Storage;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class DimensionIndexTests : DirectoryTest
{
  static DateTime Start => DateTime.UtcNow.Date.AddDays(-5);

  static void Configure(AnalyticsOptions options)
  {
    options.Storage.DimensionIndexes.Dimensions.AddRange(["source", "country", "page"]);
    options.Rollups.Definitions.Clear();
    options.Queries.MaxDegreeOfParallelism = 2;
    options.Ingestion.WorkerCount = 2;
  }

  static void View(AnalyticsEngine engine, string visitor, DateTime at, string page, string source, string country = "DE", string site = null)
  {
    Assert.That(engine.Track(new AnalyticsEvent("pageview")
    {
      Site = site, VisitorId = visitor, Timestamp = Utc(at), Url = page,
      Dimensions = { ["source"] = source, ["country"] = country }, Values = { ["score"] = 3 }
    }), Is.True);
  }

  static AnalyticsQuery Query(Filter filter, QuerySource source = QuerySource.Auto) => new()
  {
    Range = DateRange.Between(Utc(Start), Utc(Start.AddDays(2))), Source = source,
    Filters = { filter }, GroupBy = { "country" }, Metrics = { Metric.Events, Metric.Visitors, Metric.Sum("score") }
  };

  static void Same(AnalyticsResult indexed, AnalyticsResult raw)
  {
    Assert.That(indexed.Totals.Values, Is.EqualTo(raw.Totals.Values));
    Assert.That(indexed.Rows.Select(x => (string.Join('|', x.Dimensions), string.Join('|', x.Values))),
        Is.EqualTo(raw.Rows.Select(x => (string.Join('|', x.Dimensions), string.Join('|', x.Values)))));
  }

  [TestCase(1)]
  [TestCase(4)]
  public async Task CoveringReadsMatchRawAcrossCompactionReopenAndSites(int parallelism)
  {
    void Options(AnalyticsOptions options) { Configure(options); options.Queries.MaxDegreeOfParallelism = parallelism; }
    using (var engine = TestEngine.Create(directory, Options))
    {
      for (var i = 0; i < 25; ++i)
        View(engine, "v" + i, Start.AddMinutes(i), "/" + i % 3, i % 4 == 0 ? "Github" : "Google", i % 2 == 0 ? "DE" : "US");
      View(engine, "other", Start, "/other", "Github", site: "another-site");
      engine.Flush();
      var result = engine.Query(Query(Filter.Equal("source", "Github")));
      Same(result, engine.Query(Query(Filter.Equal("source", "Github"), QuerySource.RawEvents)));
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(7));
      Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
      Assert.That(result.Plan.UsedRollups, Is.False);
      Assert.That(result.Plan.Steps.All(x => x.IndexDimension == "source"), Is.True);
      await engine.CompactAsync();
      Same(engine.Query(Query(Filter.Equal("source", "Github"))), engine.Query(Query(Filter.Equal("source", "Github"), QuerySource.RawEvents)));
      Assert.That(engine.Storage.IndexedDimensions, Is.EquivalentTo(new[] { "source", "country", "page" }));
    }
    using var reopened = TestEngine.Create(directory, Options);
    var selected = reopened.Query(Query(Filter.Equal("source", "Github")));
    Assert.That(selected.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(selected.Totals[Metric.Events], Is.EqualTo(7));
    Same(selected, reopened.Query(Query(Filter.Equal("source", "Github"), QuerySource.RawEvents)));
  }

  [Test]
  public void PredicateGroupsListsDuplicatesAndFallbacksPreserveResults()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start, "/", "Github");
    View(engine, "b", Start.AddMinutes(1), "/", "Google", "US");
    View(engine, "c", Start.AddMinutes(2), "/other", "", "DE");
    engine.Flush();
    var cases = new (Filter Filter, bool Indexed)[]
    {
      (Filter.Equal("source", "Github", "Google", "Github"), true),
      (Filter.Equal("source", ""), true),
      (Filter.Equal("source", "missing"), true),
      (Filter.AllOf(Filter.Equal("source", "Github"), Filter.NotEqual("page", "/other")), true),
      (Filter.AnyOf(Filter.Equal("source", "Github"), Filter.Equal("source", "Google")), true),
      (Filter.AnyOf(Filter.Equal("source", "Github"), Filter.Equal("country", "US")), false),
      (new Filter("source", FilterOperator.Equal, true, "github"), false),
      (Filter.NotEqual("source", "Github"), false),
      (Filter.Contains("source", "hub"), false),
      (Filter.IsNotSet("campaign"), false),
      (Filter.Equal("source", Enumerable.Range(0, 65).Select(x => "s" + x).ToArray()), false),
    };
    foreach (var item in cases)
    {
      var result = engine.Query(Query(item.Filter));
      Same(result, engine.Query(Query(item.Filter, QuerySource.RawEvents)));
      Assert.That(result.Plan.UsedDimensionIndexes, Is.EqualTo(item.Indexed), item.Filter.ToString());
    }
  }

  [Test]
  public void BoundedProbesPreferTheSmallerEqualityCandidate()
  {
    using var engine = TestEngine.Create(directory, Configure);
    for (var i = 0; i < 17; ++i)
      View(engine, "v" + i, Start.AddMinutes(i), "/", "Github", i == 0 ? "US" : "DE");
    engine.Flush();
    var result = engine.Query(Query(Filter.AllOf(Filter.Equal("source", "Github"), Filter.Equal("country", "US"))));
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    Assert.That(result.Plan.Steps.Single().IndexDimension, Is.EqualTo("country"));
  }

  [Test]
  public void AddingRemovingAndReenablingIndexesBackfillsRetainedEvents()
  {
    using (var engine = TestEngine.Create(directory))
    {
      View(engine, "first", Start, "/", "Github");
      engine.Flush();
    }
    string indexDirectory;
    using (var engine = TestEngine.Create(directory, Configure))
    {
      indexDirectory = engine.Storage.GetDimensionIndex("source").Directory;
      Assert.That(engine.Query(Query(Filter.Equal("source", "Github"))).Totals[Metric.Events], Is.EqualTo(1));
    }
    using (var engine = TestEngine.Create(directory, o => o.Rollups.Definitions.Clear()))
    {
      Assert.That(Directory.EnumerateDirectories(indexDirectory), Is.Empty, "Disabled indexes release their payload copies");
      View(engine, "second", Start.AddMinutes(2), "/", "Github");
      engine.Flush();
    }
    using var reenabling = TestEngine.Create(directory, Configure);
    var result = reenabling.Query(Query(Filter.Equal("source", "Github")));
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(2));
  }

  [Test]
  public void UncleanMetadataRebuildRemovesGhostCopiesAndRestoresMissingCopies()
  {
    using (var engine = TestEngine.Create(directory, Configure))
    {
      View(engine, "a", Start, "/", "Github");
      engine.Flush();
      var storage = engine.Storage.GetDimensionIndex("source");
      using var lease = storage.Acquire(storage.GetPartitions().Single());
      using var iterator = lease.ZoneTree.CreateIterator();
      iterator.SeekFirst();
      Assert.That(iterator.Next(), Is.True);
      var value = iterator.CurrentValue;
      lease.ZoneTree.ForceDelete(iterator.CurrentKey);
      lease.ZoneTree.Upsert(DimensionIndexKeyCodec.Key(storage.KeyPrefix.Span, "default", "Github", Start.AddMinutes(1).Ticks, 123), value);
    }
    var metadata = new MetaStore(Path.Combine(directory, "meta"));
    metadata.Set("dimension-index.clean", "false");
    using var recovered = TestEngine.Create(directory, Configure);
    var result = recovered.Query(Query(Filter.Equal("source", "Github")));
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    Same(result, recovered.Query(Query(Filter.Equal("source", "Github"), QuerySource.RawEvents)));
  }

  [Test]
  public void IndexWriteFailureKeepsIngestionAndRawQueriesCorrectAndRecoversOnReopen()
  {
    var opens = 0;
    using (var engine = TestEngine.Create(directory, options =>
    {
      Configure(options);
      options.Ingestion.WorkerCount = 1;
      options.Storage.DimensionIndexes.Store.ConfigureMaintainer = _ =>
      {
        if (++opens == 1) throw new IOException("Injected secondary index write failure");
      };
    }))
    {
      View(engine, "a", Start, "/", "Github");
      View(engine, "b", Start.AddMinutes(1), "/", "Github");
      engine.Flush();
      Assert.That(engine.GetStatistics().EventsProcessed, Is.EqualTo(2));
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(1));
      var result = engine.Query(Query(Filter.Equal("source", "Github")));
      Assert.That(result.Plan.UsedDimensionIndexes, Is.False);
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(2));
    }
    using var recovered = TestEngine.Create(directory, Configure);
    Assert.That(recovered.Query(Query(Filter.Equal("source", "Github"))).Plan.UsedDimensionIndexes, Is.True);
    Assert.That(recovered.Query(Query(Filter.Equal("source", "Github"))).Totals[Metric.Events], Is.EqualTo(2));
  }

  [Test]
  public async Task ErasureRemovesEveryCopyAndRetentionMatchesPrimaryData()
  {
    using (var engine = TestEngine.Create(directory, Configure))
    {
      View(engine, "erase", Start, "/", "Github");
      View(engine, "keep", Start.AddMinutes(1), "/", "Github");
      View(engine, "recent", DateTime.UtcNow.AddMinutes(-2), "/", "Github");
      engine.Flush();
      Assert.That(await engine.EraseVisitorAsync("erase"), Is.EqualTo(1));
      Assert.That(engine.Query(Query(Filter.Equal("source", "Github"))).Totals[Metric.Events], Is.EqualTo(1));
      await engine.CompactAsync();
    }
    using var retained = TestEngine.Create(directory, options => { Configure(options); options.Retention.RawEvents = TimeSpan.FromDays(2); });
    retained.ApplyRetention();
    var query = Query(Filter.Equal("source", "Github"));
    query.Range = DateRange.LastDays(10);
    var result = retained.Query(query);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    Assert.That(result.Plan.IsComplete, Is.False, "Indexes keep raw retention warnings");
    query.Source = QuerySource.RawEvents;
    Same(result, retained.Query(query));
  }

  [TestCase(1)]
  [TestCase(4)]
  public void SessionReplayAcrossBothBoundariesMatchesRaw(int parallelism)
  {
    using var engine = TestEngine.Create(directory, options => { Configure(options); options.Queries.MaxDegreeOfParallelism = parallelism; });
    View(engine, "a", Start.AddMinutes(5), "/start", "Github");
    View(engine, "a", Start.AddMinutes(-10), "/late", "Google");
    View(engine, "a", Start.AddMinutes(25), "/continue", "Google");
    View(engine, "other", Start.AddMinutes(6), "/start", "Google");
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Range = DateRange.Between(Utc(Start), Utc(Start.AddMinutes(15))),
      Filters = { Filter.Equal("source", "Github", "Google", "Github") },
      GroupBy = { "source" }, Metrics = { Metric.Events, Metric.Visitors, Metric.Sessions, Metric.Bounces, Metric.PageViewsPerSession, Metric.AverageSessionDuration }
    };
    var indexed = engine.Query(query);
    query.Source = QuerySource.RawEvents;
    Same(indexed, engine.Query(query));
    Assert.That(indexed.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(indexed.Totals[Metric.Events], Is.EqualTo(2));
    Assert.That(indexed.Totals[Metric.PageViewsPerSession], Is.EqualTo(2));
  }

  [Test]
  public void PageFiltersKeepCompleteSessionAndPageFlowReplay()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start, "/entry", "Github");
    View(engine, "a", Start.AddMinutes(1), "/exit", "Github");
    engine.Flush();
    var query = new AnalyticsQuery { Range = Day(Start), Filters = { Filter.Equal("page", "/entry") }, Metrics = { Metric.Events, Metric.Entrances, Metric.Exits } };
    var result = engine.Query(query);
    query.Source = QuerySource.RawEvents;
    Same(result, engine.Query(query));
    Assert.That(result.Plan.UsedDimensionIndexes, Is.False, "Event-level filters cannot truncate session reconstruction");
  }

  [Test]
  public void FunnelsKeepTimeOrderAndIndexedPayloadIsAnExactCopy()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start, "/entry", "Github");
    View(engine, "b", Start, "/entry", "Google");
    View(engine, "a", Start.AddMinutes(1), "/goal", "Github");
    View(engine, "b", Start.AddMinutes(2), "/goal", "Google");
    engine.Flush();
    var funnel = new FunnelQuery { Range = Day(Start), Filters = { Filter.Equal("source", "Github", "Google", "Github") }, Steps = { FunnelStep.Page("Entry", "/entry"), FunnelStep.Page("Goal", "/goal") } };
    var result = engine.Query(funnel);
    Assert.That(result.Steps.Select(x => x.Visitors), Is.EqualTo(new long[] { 2, 2 }));
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    var primary = new Dictionary<long, byte[]>();
    foreach (var partition in engine.Storage.RawEvents.GetPartitions())
    {
      using var lease = engine.Storage.RawEvents.Acquire(partition);
      using var iterator = lease.ZoneTree.CreateIterator();
      iterator.SeekFirst();
      while (iterator.Next()) primary.Add(DimensionIndexKeyCodec.Sequence(iterator.CurrentKey.Span), iterator.CurrentValue.ToArray());
    }
    var index = engine.Storage.GetDimensionIndex("source");
    var count = 0;
    foreach (var partition in index.GetPartitions())
    {
      using var lease = index.Acquire(partition);
      using var iterator = lease.ZoneTree.CreateIterator();
      iterator.Seek(index.KeyPrefix.ToArray());
      while (iterator.Next() && iterator.CurrentKey.Span.StartsWith(index.KeyPrefix.Span))
      {
        Assert.That(iterator.CurrentValue.ToArray(), Is.EqualTo(primary[DimensionIndexKeyCodec.Sequence(iterator.CurrentKey.Span)]));
        ++count;
      }
    }
    Assert.That(count, Is.EqualTo(4));
  }

  [Test]
  public void MultiValueFunnelsMergeEventsInTimeOrderAcrossSessions()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start, "/entry", "Google");
    View(engine, "a", Start.AddMinutes(35), "/goal", "Github");
    engine.Flush();
    var query = new FunnelQuery
    {
      Range = Day(Start), Window = TimeSpan.FromHours(1),
      Filters = { Filter.Equal("source", "Github", "Google") },
      Steps = { FunnelStep.Page("Entry", "/entry"), FunnelStep.Page("Goal", "/goal") }
    };
    var result = engine.Query(query);
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(result.Steps.Select(x => x.Visitors), Is.EqualTo(new long[] { 1, 1 }));
  }

  [Test]
  public void EqualityIndexSkipsNonmatchingPayloadsAndCancellationIsHonored()
  {
    using var engine = TestEngine.Create(directory, Configure);
    for (var i = 0; i < 25; ++i)
      View(engine, "v" + i, Start.AddMinutes(i), "/", i == 5 ? "Github" : "Google");
    engine.Flush();
    var selection = engine.DimensionIndexes.Select("default", Start.Ticks, Start.AddDays(1).Ticks,
        [Filter.Equal("source", "Github")], _ => true, CancellationToken.None);
    var states = engine.RawEventStore.ScanIndexed(selection, "default", Start.Ticks, Start.AddDays(1).Ticks,
        2, () => new List<long>(), (state, ticks, _) => state.Add(ticks), CancellationToken.None, out var used);
    Assert.That(used, Is.True);
    Assert.That(states.SelectMany(x => x), Is.EqualTo(new[] { Start.AddMinutes(5).Ticks }));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Assert.Throws<OperationCanceledException>(() => engine.Query(Query(Filter.Equal("source", "Github")), cancellation.Token));
  }

  [Test]
  public void NonDurableIndexesAreRebuiltEvenAfterOrderlyShutdown()
  {
    void Options(AnalyticsOptions options)
    {
      Configure(options);
      options.Storage.DimensionIndexes.Store.ConfigureWriteAheadLog = wal => wal.WriteAheadLogMode = ZoneTree.Options.WriteAheadLogMode.None;
    }
    using (var engine = TestEngine.Create(directory, Options))
    {
      View(engine, "a", Start, "/", "Github");
      engine.Flush();
      Assert.That(engine.Query(Query(Filter.Equal("source", "Github"))).Totals[Metric.Events], Is.EqualTo(1));
    }
    using var recovered = TestEngine.Create(directory, Options);
    var result = recovered.Query(Query(Filter.Equal("source", "Github")));
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
  }

  [Test]
  public void ComparisonAndSessionLevelIndexUseMatchForcedRaw()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start, "/entry", "Github");
    View(engine, "a", Start.AddMinutes(1), "/goal", "Github");
    View(engine, "b", Start.AddDays(1), "/entry", "Github");
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Range = Day(Start.AddDays(1)), Compare = ComparisonPeriod.PreviousPeriod,
      Filters = { Filter.Equal("source", "Github") }, GroupBy = { "country" },
      Metrics = { Metric.Events, Metric.Visitors, Metric.Sessions, Metric.BounceRate, Metric.AverageSessionDuration }
    };
    var indexed = engine.Query(query);
    query.Source = QuerySource.RawEvents;
    var raw = engine.Query(query);
    Same(indexed, raw);
    Same(indexed.Comparison, raw.Comparison);
    Assert.That(indexed.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(indexed.Comparison.Plan.UsedDimensionIndexes, Is.True);
  }

  [Test]
  public void IndexOptionsAreClonedAndValidatedAndKeysCannotAlias()
  {
    var options = TestEngine.Options(directory, Configure);
    var clone = options.Clone();
    options.Storage.DimensionIndexes.Dimensions.Clear();
    Assert.That(clone.Storage.DimensionIndexes.Dimensions, Has.Count.EqualTo(3));
    options.Storage.DimensionIndexes.Dimensions.Add("source");
    options.Storage.StoreRawEvents = false;
    Assert.Throws<ArgumentException>(() => new AnalyticsEngine(options));
    options.Storage.StoreRawEvents = true;
    options.Storage.DimensionIndexes.Dimensions.Add("source");
    Assert.Throws<ArgumentException>(() => new AnalyticsEngine(options));
    var prefix = DimensionIndexKeyCodec.Namespace("source");
    Assert.That(DimensionIndexKeyCodec.Key(prefix, "default", "a\0b", 123, 456).ToArray(),
        Is.Not.EqualTo(DimensionIndexKeyCodec.Key(prefix, "default", "a", 123, 456).ToArray()));
  }
}
