using ZoneTree.Analytics.Storage;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class RollupIndexTests : DirectoryTest
{
  static DateTime Start => DateTime.UtcNow.Date.AddDays(-5);

  static void Configure(AnalyticsOptions options)
  {
    options.Queries.MaxDegreeOfParallelism = 2;
    options.Ingestion.WorkerCount = 2;
    options.Storage.DimensionIndexes.Dimensions.Add("source");
    foreach (var group in new[] { "country", "device", "page" })
      options.Storage.RollupIndexes.Definitions.Add(new("source", group));
    options.Rollups.ValueAggregates["load_time"] = ValueAggregates.Percentiles | ValueAggregates.FirstAndLast;
    options.Rollups.DistinctDimensions.Add("page");
  }

  static void View(AnalyticsEngine engine, string visitor, DateTime at, string page, string source, string site = null)
  {
    Assert.That(engine.Track(new AnalyticsEvent("pageview")
    {
      Site = site, VisitorId = visitor, Timestamp = Utc(at), Url = page,
      UserAgent = UserAgents.ChromeWindows,
      Dimensions = { ["source"] = source, ["country"] = "DE" }, Values = { ["load_time"] = 120 }
    }), Is.True);
  }

  static AnalyticsQuery Query(Filter filter, string group = "country", QuerySource source = QuerySource.Auto) => new()
  {
    Range = Day(Start), Source = source, Filters = { filter }, GroupBy = { group },
    Metrics = { Metric.Events, Metric.Visitors, Metric.Sum("load_time"), Metric.Average("load_time") }
  };

  static void Same(AnalyticsResult indexed, AnalyticsResult raw)
  {
    Assert.That(indexed.Totals.Values, Is.EqualTo(raw.Totals.Values));
    Assert.That(indexed.Rows.Select(row => (string.Join('|', row.Dimensions), string.Join('|', row.Values))),
        Is.EqualTo(raw.Rows.Select(row => (string.Join('|', row.Dimensions), string.Join('|', row.Values)))));
  }

  [TestCase(false)]
  [TestCase(true)]
  public void SampleOverviewCoversAllSingleDimensionFiltersIncludingExistingSourceOnlyStores(bool sourceOnlyStore)
  {
    void Options(AnalyticsOptions options)
    {
      options.Queries.MaxDegreeOfParallelism = 2;
      options.Ingestion.WorkerCount = 1;
      DashboardAnalyticsOptions.ConfigureIndexes(options);
    }
    using (var seeded = TestEngine.Create(directory, options =>
    {
      if (!sourceOnlyStore) Options(options);
      else
      {
        options.Queries.MaxDegreeOfParallelism = 2;
        options.Ingestion.WorkerCount = 1;
        foreach (var group in new[] { "country", "device", "page" })
          options.Storage.RollupIndexes.Definitions.Add(new("source", group));
      }
    }))
    {
      for (var i = 0; i < 8; ++i)
        Assert.That(seeded.Track(new AnalyticsEvent("pageview")
        {
          VisitorId = "v" + i, Timestamp = Utc(Start.AddMinutes(i)), Url = i % 3 == 0 ? "/blog" : "/pricing",
          UserAgent = i < 4 ? UserAgents.SafariIPhone : UserAgents.ChromeWindows,
          Dimensions = { ["source"] = i % 2 == 0 ? "GitHub" : "Google", ["country"] = i / 2 % 2 == 0 ? "US" : "DE" }
        }), Is.True);
      seeded.Flush();
    }
    using var engine = TestEngine.Create(directory, Options);
    foreach (var (dimension, value, count) in new[]
        { ("source", "GitHub", 4), ("country", "US", 4), ("device", "Mobile", 4), ("page", "/blog", 3) })
    {
      var filter = Filter.Equal(dimension, value);
      var request = new WebReportRequest
      {
        Range = Day(Start), Filters = { filter }, IncludeSessionMetrics = false, IncludePageFlowMetrics = false
      };
      var summary = engine.Web.GetSummary(request);
      Assert.That(summary.Summary.PageViews, Is.EqualTo(count));
      Assert.That(summary.Plan.UsedRawEvents, Is.False);
      Assert.That(engine.Web.GetTimeseries(request, TimeInterval.Day).Plan.UsedRawEvents, Is.False);
      foreach (var group in new[] { "source", "country", "device", "page" })
      {
        var plan = group == "page" ? engine.Web.GetPages(request).Plan : engine.Web.GetBreakdown(request, group).Plan;
        Assert.That(plan.UsedRollups, Is.True, $"{dimension} -> {group}");
        Assert.That(plan.UsedRawEvents, Is.False, $"{dimension} -> {group}");
        if (dimension != group)
        {
          Assert.That(plan.UsedRollupIndexes, Is.True, $"{dimension} -> {group}");
          Assert.That(plan.Steps.All(step => step.IndexDimension == dimension), Is.True);
        }
        var query = new AnalyticsQuery
        {
          Range = Day(Start), Filters = { filter }, GroupBy = { group }, Metrics = { Metric.PageViews, Metric.Visitors }
        };
        var rolled = engine.Query(query);
        query.Source = QuerySource.RawEvents;
        Same(rolled, engine.Query(query));
        Assert.That(rolled.Totals[Metric.PageViews], Is.EqualTo(count));
      }
    }
    Assert.That(engine.Options.Rollups.Definitions.Count(definition => definition.Dimensions.Count == 2 &&
        definition.Dimensions.All(dimension => dimension is "source" or "country" or "device" or "page")), Is.EqualTo(6));
  }

  [TestCase(Granularity.Day)]
  [TestCase(Granularity.Hour)]
  [TestCase(Granularity.Minute)]
  public async Task CoveringRollupsPreserveValuesSitesAndEqualityListsAcrossReopen(Granularity granularity)
  {
    void Options(AnalyticsOptions options) { Configure(options); options.Rollups.Granularities = granularity; }
    using (var engine = TestEngine.Create(directory, Options))
    {
      foreach (var source in new[] { "GitHub", "GitHubX", "", "γ😀" })
        View(engine, source, Start.AddMinutes(2), "/docs", source);
      View(engine, "other", Start.AddMinutes(2), "/other", "GitHub", "other-site");
      engine.Flush();
      foreach (var filter in new[] { Filter.Equal("source", "GitHub"), Filter.Equal("source", ""), Filter.Equal("source", "γ😀"),
          Filter.Equal("source", "GitHub", "γ😀", "GitHub"), Filter.AnyOf(Filter.Equal("source", "GitHub"), Filter.Equal("source", "GitHubX")) })
      {
        var result = engine.Query(Query(filter));
        Same(result, engine.Query(Query(filter, source: QuerySource.RawEvents)));
        Assert.That(result.Plan.UsedRollupIndexes, Is.True);
        Assert.That(result.Plan.UsedRawEvents, Is.False);
        Assert.That(result.Plan.Steps.All(step => step.IndexDimension == "source"), Is.True);
      }
      await engine.CompactAsync();
      Assert.That(engine.Storage.GetRollupIndex(granularity, "source", "country"), Is.Not.Null);
    }
    using var reopened = TestEngine.Create(directory, Options);
    var selected = reopened.Query(Query(Filter.Equal("source", "GitHub")));
    Same(selected, reopened.Query(Query(Filter.Equal("source", "GitHub"), source: QuerySource.RawEvents)));
    Assert.That(selected.Plan.UsedRollupIndexes, Is.True);
    Assert.That(selected.Totals[Metric.Events], Is.EqualTo(1));
  }

  [Test]
  public void OverviewAndDetailedReportsUseRollupsAndPreserveSessionMetrics()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start.AddMinutes(1), "/docs", "GitHub");
    View(engine, "a", Start.AddMinutes(2), "/pricing", "Google");
    View(engine, "b", Start.AddMinutes(4), "/docs", "Google");
    engine.Flush();
    var request = new WebReportRequest { Range = Day(Start), Filters = { Filter.Equal("source", "GitHub") } };
    foreach (var group in new[] { "country", "device" })
    {
      var full = engine.Web.GetBreakdown(request, group);
      Assert.That(full.Plan.UsedRollupIndexes, Is.True);
      Assert.That(full.Plan.UsedRawEvents, Is.False);
      Assert.That(full.Rows.Single().PageViews, Is.EqualTo(2));
      Assert.That(full.Rows.Single().Sessions, Is.EqualTo(1));
      Assert.That(full.Rows.Single().BounceRate, Is.EqualTo(0));
      Assert.That(full.Rows.Single().AverageSessionDuration, Is.EqualTo(TimeSpan.FromMinutes(1)));
      request.IncludeSessionMetrics = false;
      var projected = engine.Web.GetBreakdown(request, group);
      Assert.That(projected.Plan.UsedRollupIndexes, Is.True);
      Assert.That(projected.Rows.Single().Sessions, Is.Null);
      request.IncludeSessionMetrics = true;
    }
    var pages = engine.Web.GetPages(request);
    Assert.That(pages.Plan.UsedRollupIndexes, Is.True);
    Assert.That(pages.Plan.UsedRawEvents, Is.False);
    Assert.That(pages.Rows.Single(row => row.Page == "/docs").Entrances, Is.EqualTo(1));
    Assert.That(pages.Rows.Single(row => row.Page == "/pricing").Exits, Is.EqualTo(1));
  }

  [Test]
  public void EnablingIndexesBackfillsOnlyJointDefinitionsAndResumesExistingSessions()
  {
    using (var engine = TestEngine.Create(directory))
    {
      View(engine, "a", Start.AddMinutes(1), "/docs", "GitHub");
      View(engine, "a", Start.AddMinutes(2), "/pricing", "Google");
      View(engine, "b", Start.AddMinutes(4), "/docs", "Google");
      engine.Flush();
    }
    using (var engine = TestEngine.Create(directory, Configure))
    {
      foreach (var group in new[] { "country", "device", "page" })
      {
        var result = engine.Query(Query(Filter.Equal("source", "GitHub"), group));
        Same(result, engine.Query(Query(Filter.Equal("source", "GitHub"), group, QuerySource.RawEvents)));
        Assert.That(result.Plan.UsedRollupIndexes, Is.True);
        Assert.That(result.Plan.UsedRawEvents, Is.False);
      }
      var unfiltered = engine.Web.GetSummary(new WebReportRequest { Range = Day(Start) });
      Assert.That(unfiltered.Summary.PageViews, Is.EqualTo(3), "Backfill must not double existing single-dimension rollups.");
      View(engine, "a", Start.AddMinutes(3), "/continue", "Google");
      engine.Flush();
      var query = Query(Filter.Equal("source", "GitHub"));
      query.Metrics.AddRange([Metric.Sessions, Metric.BounceRate, Metric.AverageSessionDuration]);
      var resultWithSessions = engine.Query(query);
      query.Source = QuerySource.RawEvents;
      Same(resultWithSessions, engine.Query(query));
      var pages = engine.Web.GetPages(new WebReportRequest { Range = Day(Start), Filters = { Filter.Equal("source", "GitHub") } });
      Assert.That(pages.Rows.Single(row => row.Page == "/pricing").Exits, Is.EqualTo(0));
      Assert.That(pages.Rows.Single(row => row.Page == "/continue").Exits, Is.EqualTo(1));
    }
    using var reopened = TestEngine.Create(directory, Configure);
    var final = reopened.Query(Query(Filter.Equal("source", "GitHub")));
    Assert.That(final.Totals[Metric.Events], Is.EqualTo(3));
    Assert.That(final.Plan.UsedRollupIndexes, Is.True);
  }

  [Test]
  public void UncleanOrMissingCopiesAreRebuiltFromAuthoritativeRollups()
  {
    using (var engine = TestEngine.Create(directory, Configure))
    {
      View(engine, "a", Start, "/docs", "GitHub");
      engine.Flush();
      using var lease = engine.Storage.GetRollupIndex(Granularity.Day, "source", "country").Acquire(
          engine.Storage.GetRollupIndex(Granularity.Day, "source", "country").GetPartitions().Single());
      using var iterator = lease.ZoneTree.CreateIterator();
      iterator.SeekFirst();
      Assert.That(iterator.Next(), Is.True);
      lease.ZoneTree.ForceDelete(iterator.CurrentKey);
    }
    new MetaStore(Path.Combine(directory, "meta")).Set("rollup-index.clean", "false");
    using var reopened = TestEngine.Create(directory, Configure);
    var result = reopened.Query(Query(Filter.Equal("source", "GitHub")));
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    Same(result, reopened.Query(Query(Filter.Equal("source", "GitHub"), source: QuerySource.RawEvents)));
  }

  [Test]
  public void FailedCopyKeepsPrimaryRollupsCorrectAndRecoversOnReopen()
  {
    var opens = 0;
    using (var engine = TestEngine.Create(directory, options =>
    {
      Configure(options);
      options.Storage.RollupIndexes.Store.ConfigureMaintainer = _ =>
      {
        if (++opens == 2) throw new IOException("Injected rollup index write failure");
      };
    }))
    {
      View(engine, "a", Start, "/docs", "GitHub");
      View(engine, "b", Start.AddMinutes(1), "/docs", "GitHub");
      engine.Flush();
      var result = engine.Query(Query(Filter.Equal("source", "GitHub")));
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(2));
      Assert.That(result.Plan.UsedRollups, Is.True);
      Assert.That(result.Plan.UsedRollupIndexes, Is.False);
      Assert.That(engine.GetStatistics().Errors, Is.EqualTo(1));
    }
    using var recovered = TestEngine.Create(directory, Configure);
    var restored = recovered.Query(Query(Filter.Equal("source", "GitHub")));
    Assert.That(restored.Totals[Metric.Events], Is.EqualTo(2));
    Assert.That(restored.Plan.UsedRollupIndexes, Is.True);
  }

  [Test]
  public void PendingHistoricalBackfillIsRepeatedWithoutDoubleCounting()
  {
    using (var engine = TestEngine.Create(directory, Configure))
    {
      View(engine, "a", Start, "/docs", "GitHub");
      engine.Flush();
    }
    new MetaStore(Path.Combine(directory, "meta")).Set("rollup-index.backfill-pending", "country,source");
    using var recovered = TestEngine.Create(directory, Configure);
    var query = Query(Filter.Equal("source", "GitHub"));
    var result = recovered.Query(query);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
    Same(result, recovered.Query(Query(Filter.Equal("source", "GitHub"), source: QuerySource.RawEvents)));
    Assert.That(recovered.Web.GetSummary(new WebReportRequest { Range = Day(Start) }).Summary.PageViews, Is.EqualTo(1));
  }

  [Test]
  public void RetentionOfCopiesMatchesTheirRollupTierAndRawExpiryDoesNotExpireDailyCopies()
  {
    using (var engine = TestEngine.Create(directory, Configure))
    {
      View(engine, "old", Start, "/docs", "GitHub");
      View(engine, "recent", DateTime.UtcNow.AddMinutes(-2), "/docs", "GitHub");
      engine.Flush();
    }
    using (var engine = TestEngine.Create(directory, options => { Configure(options); options.Retention.RawEvents = TimeSpan.FromDays(2); }))
    {
      engine.ApplyRetention();
      Assert.That(engine.Query(Query(Filter.Equal("source", "GitHub"))).Totals[Metric.Events], Is.EqualTo(1));
      Assert.That(engine.Query(Query(Filter.Equal("source", "GitHub"))).Plan.UsedRollupIndexes, Is.True);
    }
    using var expired = TestEngine.Create(directory, options =>
    {
      Configure(options);
      options.Retention.RawEvents = TimeSpan.FromDays(2);
      options.Retention.DayRollups = TimeSpan.FromDays(2);
      options.Retention.HourRollups = TimeSpan.FromDays(2);
    });
    expired.ApplyRetention();
    var result = expired.Query(Query(Filter.Equal("source", "GitHub")));
    Assert.That(result.Totals[Metric.Events], Is.Zero);
    foreach (var granularity in new[] { Granularity.Day, Granularity.Hour })
    {
      var storage = expired.Storage.GetRollupIndex(granularity, "source", "country");
      foreach (var partition in storage.GetPartitions())
      {
        using var lease = storage.Acquire(partition);
        using var iterator = lease.ZoneTree.CreateIterator();
        iterator.SeekFirst();
        while (iterator.Next())
        {
          var original = StorageKeyCodec.DecodeRollup(RollupIndexKeyCodec.OriginalKey(iterator.CurrentKey.Span));
          Assert.That(original.BucketTicks, Is.GreaterThanOrEqualTo(DateTime.UtcNow.Date.AddDays(-2).Ticks));
        }
      }
    }
  }

  [Test]
  public void ExistingJointRollupsCanBeIndexedWithoutRawStorageAndUnsupportedPredicatesUsePrimary()
  {
    using (var engine = TestEngine.Create(directory, options =>
    {
      options.Rollups.Definitions.Add(new("source", "country"));
      options.Storage.StoreRawEvents = false;
    }))
    {
      View(engine, "a", Start, "/docs", "GitHub"); engine.Flush();
    }
    using var indexed = TestEngine.Create(directory, options =>
    {
      options.Rollups.Definitions.Add(new("source", "country"));
      options.Storage.StoreRawEvents = false;
      options.Storage.RollupIndexes.Definitions.Add(new("source", "country"));
    });
    Assert.That(indexed.Query(Query(Filter.Equal("source", "GitHub"))).Plan.UsedRollupIndexes, Is.True);
    foreach (var filter in new[] { Filter.Contains("source", "Hub"), new Filter("source", FilterOperator.Equal, true, "github") })
    {
      var result = indexed.Query(Query(filter));
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
      Assert.That(result.Plan.UsedRollups, Is.True);
      Assert.That(result.Plan.UsedRollupIndexes, Is.False);
    }
  }

  [Test]
  public void RemovingAndReenablingCopiesAndDefinitionsDoesNotReuseStaleHistory()
  {
    string indexDirectory;
    using (var engine = TestEngine.Create(directory, Configure))
    {
      View(engine, "a", Start, "/docs", "GitHub"); engine.Flush();
      indexDirectory = engine.Storage.GetRollupIndex(Granularity.Day, "source", "country").Directory;
    }
    using (var disabled = TestEngine.Create(directory))
    {
      Assert.That(Directory.EnumerateDirectories(indexDirectory), Is.Empty);
      View(disabled, "b", Start.AddMinutes(1), "/docs", "GitHub"); disabled.Flush();
    }
    using var reenabled = TestEngine.Create(directory, Configure);
    var result = reenabled.Query(Query(Filter.Equal("source", "GitHub")));
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(2));
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
  }

  [Test]
  public async Task ConcurrentFlushesCopyAbsoluteValuesWithoutReapplyingDeltas()
  {
    using var engine = TestEngine.Create(directory, Configure);
    await Task.WhenAll(Enumerable.Range(0, 4).Select(async worker =>
    {
      for (var i = 0; i < 6; ++i)
      {
        View(engine, $"w{worker}-{i}", Start.AddMinutes(i), "/docs", "GitHub");
        await engine.FlushAsync();
      }
    }));
    var result = engine.Query(Query(Filter.Equal("source", "GitHub")));
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(24));
    Same(result, engine.Query(Query(Filter.Equal("source", "GitHub"), source: QuerySource.RawEvents)));
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
  }

  [Test]
  public void CopiesKeepCompleteOriginalKeysAndSerializedValuesAndRequireNoPrimaryLookups()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start, "/docs", "GitHub"); engine.Flush();
    var primary = new Dictionary<string, byte[]>();
    var serializer = new RollupValueSerializer();
    foreach (var partition in engine.Storage.GetRollups(Granularity.Day).GetPartitions())
    {
      using var lease = engine.Storage.GetRollups(Granularity.Day).Acquire(partition);
      using var iterator = lease.ZoneTree.CreateIterator(); iterator.SeekFirst();
      while (iterator.Next()) primary[Convert.ToHexString(iterator.CurrentKey.Span)] = serializer.Serialize(iterator.CurrentValue).ToArray();
    }
    var copies = 0;
    var index = engine.Storage.GetRollupIndex(Granularity.Day, "source", "country");
    foreach (var partition in index.GetPartitions())
    {
      using var lease = index.Acquire(partition);
      using var iterator = lease.ZoneTree.CreateIterator(); iterator.SeekFirst();
      while (iterator.Next())
      {
        ++copies;
        var original = RollupIndexKeyCodec.OriginalKey(iterator.CurrentKey.Span);
        Assert.That(serializer.Serialize(iterator.CurrentValue).ToArray(), Is.EqualTo(primary[Convert.ToHexString(original)]));
      }
    }
    Assert.That(copies, Is.GreaterThan(0));
    var result = engine.Query(Query(Filter.Equal("source", "GitHub")));
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
    Assert.That(result.Plan.UsedRawEvents, Is.False);
  }

  [Test]
  public void PartialRangesComparisonsAndOptInAggregatesStillMatchRaw()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start.AddMinutes(1), "/docs", "GitHub");
    View(engine, "a", Start.AddHours(2).AddMinutes(1), "/docs", "GitHub");
    View(engine, "previous", Start.AddDays(-1).AddMinutes(1), "/docs", "GitHub");
    engine.Flush();
    var query = Query(Filter.Equal("source", "GitHub"));
    query.Range = Range(Start.AddSeconds(30), Start.AddHours(3).AddSeconds(30));
    query.Compare = ComparisonPeriod.PreviousYear;
    query.Metrics.AddRange([Metric.Percentile("load_time", 95), Metric.First("load_time"), Metric.Last("load_time"), Metric.Distinct("page")]);
    var result = engine.Query(query);
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
    Assert.That(result.Plan.UsedRawEvents, Is.True, "Non-aligned range boundaries still read raw events.");
    query.Source = QuerySource.RawEvents;
    var raw = engine.Query(query);
    Same(result, raw);
    Same(result.Comparison, raw.Comparison);
  }

  [Test]
  public void BackfillPreservesLateEventsSessionsAcrossBucketsAndMultipleSites()
  {
    using (var engine = TestEngine.Create(directory))
    {
      foreach (var site in new[] { "default", "other-site" })
      {
        View(engine, "a", Start.AddMinutes(5), "/start", "GitHub", site);
        View(engine, "a", Start.AddMinutes(-10), "/late", "Google", site);
        View(engine, "a", Start.AddMinutes(25), "/exit", "Google", site);
        View(engine, "b", Start.AddHours(2), "/start", "GitHub", site);
      }
      engine.Flush();
    }
    using var migrated = TestEngine.Create(directory, Configure);
    foreach (var site in new[] { "default", "other-site" })
    {
      var query = Query(Filter.Equal("source", "GitHub"));
      query.Site = site;
      query.Range = Range(Start.AddDays(-1), Start.AddDays(1));
      query.Metrics.AddRange([Metric.Sessions, Metric.BounceRate, Metric.AverageSessionDuration, Metric.PageViewsPerSession]);
      var indexed = migrated.Query(query);
      Assert.That(indexed.Plan.UsedRollupIndexes, Is.True);
      query.Source = QuerySource.RawEvents;
      Same(indexed, migrated.Query(query));
    }
  }

  [Test]
  public void ConfigurationIsOptInValidatedAndCopied()
  {
    var options = TestEngine.Options(directory);
    Assert.That(options.Storage.RollupIndexes.Definitions, Is.Empty);
    options.Storage.RollupIndexes.Definitions.Add(new("source", "country"));
    var cloned = options.Clone();
    options.Storage.RollupIndexes.Definitions.Clear();
    Assert.That(cloned.Storage.RollupIndexes.Definitions, Has.Count.EqualTo(1));
    using (var engine = new AnalyticsEngine(cloned))
    {
      Assert.That(cloned.Rollups.Definitions.Any(definition => definition.Id == "country,source"), Is.False,
          "Generated joint rollups only modify the engine's private configuration.");
      Assert.That(engine.Options.Rollups.Definitions.Any(definition => definition.Id == "country,source"), Is.True);
    }
    options.Storage.RollupIndexes.Definitions.Add(null);
    Assert.That(() => options.Validate(), Throws.ArgumentException);
    options.Storage.RollupIndexes.Definitions.Clear();
    options.Storage.RollupIndexes.Definitions.Add(new("source", "country"));
    options.Storage.RollupIndexes.Definitions.Add(new("source", "country"));
    Assert.That(() => options.Validate(), Throws.ArgumentException);
    Assert.That(() => new RollupIndexDefinition("source", "source"), Throws.ArgumentException);
    Assert.That(() => new RollupIndexDefinition("$hour_of_day", "country"), Throws.ArgumentException);
  }

  [Test]
  public void NonDurableCopiesAreRebuiltOnEveryOpen()
  {
    void Options(AnalyticsOptions options)
    {
      Configure(options);
      options.Storage.RollupIndexes.Store.ConfigureWriteAheadLog = wal => wal.WriteAheadLogMode = ZoneTree.Options.WriteAheadLogMode.None;
    }
    using (var engine = TestEngine.Create(directory, Options))
    {
      View(engine, "a", Start, "/docs", "GitHub"); engine.Flush();
      Assert.That(engine.Query(Query(Filter.Equal("source", "GitHub"))).Plan.UsedRollupIndexes, Is.True);
    }
    Assert.That(new MetaStore(Path.Combine(directory, "meta")).Get("rollup-index.clean"), Is.EqualTo("false"));
    using var reopened = TestEngine.Create(directory, Options);
    var result = reopened.Query(Query(Filter.Equal("source", "GitHub")));
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
  }
}
