using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class SharedDimensionIndexTests : DirectoryTest
{
  static DateTime Start => DateTime.UtcNow.Date.AddDays(-5);
  static readonly string[] Dimensions = ["source", "country", "device", "page"];

  static void Configure(AnalyticsOptions options)
  {
    options.Sessions.Enabled = false;
    options.Rollups.Definitions.Clear();
    options.Ingestion.WorkerCount = 2;
    options.Queries.MaxDegreeOfParallelism = 2;
    options.Storage.DimensionIndexes.Dimensions.AddRange(Dimensions);
  }

  static void View(AnalyticsEngine engine, string visitor, DateTime at, string source = "US", string country = "US", string site = null, bool mobile = false) =>
      Assert.That(engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = visitor, Site = site, Timestamp = Utc(at), Url = "/blog",
        UserAgent = mobile ? UserAgents.SafariIPhone : UserAgents.ChromeWindows,
        Dimensions = { ["source"] = source, ["country"] = country }, Values = { ["score"] = 3 }
      }), Is.True);

  static AnalyticsQuery Query(string dimension, string value, string site = null) => new()
  {
    Site = site, Range = Range(Start, Start.AddDays(4)), Filters = { Filter.Equal(dimension, value) },
    GroupBy = { "country" }, Metrics = { Metric.Events, Metric.Visitors, Metric.Sum("score") }
  };

  static long CountNamespace(AnalyticsPartitionedStorage<Memory<byte>, Memory<byte>> storage)
  {
    long count = 0;
    foreach (var partition in storage.GetPartitions())
    {
      using var lease = storage.Acquire(partition);
      using var iterator = lease.ZoneTree.CreateIterator(); iterator.Seek(storage.KeyPrefix.ToArray());
      while (iterator.Next() && iterator.CurrentKey.Span.StartsWith(storage.KeyPrefix.Span)) count++;
    }
    return count;
  }

  [Test]
  public void FourNamespacesShareOneTreeWithExactPayloadCopiesAndDistinctStatistics()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start.AddMinutes(1), country: "DE");
    View(engine, "b", Start.AddMinutes(1), source: "Google", mobile: true);
    View(engine, "c", Start.AddMinutes(1), site: "other-site");
    engine.Flush();
    Assert.That(engine.DimensionIndexes.Indexes, Has.Count.EqualTo(4));
    Assert.That(engine.DimensionIndexes.AllStores.Count(), Is.EqualTo(1));
    Assert.That(engine.DimensionIndexes.Indexes.Select(index => index.Store).Distinct().Count(), Is.EqualTo(1));
    var primary = new Dictionary<long, byte[]>();
    foreach (var partition in engine.Storage.RawEvents.GetPartitions())
    {
      using var lease = engine.Storage.RawEvents.Acquire(partition);
      using var iterator = lease.ZoneTree.CreateIterator(); iterator.SeekFirst();
      while (iterator.Next()) primary.Add(DimensionIndexKeyCodec.Sequence(iterator.CurrentKey.Span), iterator.CurrentValue.ToArray());
    }
    foreach (var dimension in Dimensions)
    {
      var index = engine.Storage.GetDimensionIndex(dimension);
      Assert.That(index.Directory, Is.EqualTo(Path.Combine(directory, "indexes")));
      Assert.That(index.KeyPrefix.IsEmpty, Is.False);
      Assert.That(CountNamespace(index), Is.EqualTo(3));
    }
    var store = engine.DimensionIndexes.Store;
    using (var lease = store.Acquire(store.ListPartitions().Single()))
    {
      Assert.That(lease.Tree.Maintenance.TotalRecordCount, Is.EqualTo(12));
      using var iterator = lease.Tree.CreateIterator(); iterator.SeekFirst();
      while (iterator.Next())
        Assert.That(iterator.CurrentValue.ToArray(), Is.EqualTo(primary[DimensionIndexKeyCodec.Sequence(iterator.CurrentKey.Span)]));
    }
    var statistics = engine.GetStatistics();
    Assert.That(statistics.OpenPartitions, Is.EqualTo(2), "One primary raw partition and one shared index partition.");
    var indexStatistics = statistics.Partitions.Where(partition => partition.Directory.StartsWith("indexes/", StringComparison.Ordinal)).ToArray();
    Assert.That(indexStatistics, Has.Length.EqualTo(1));
    Assert.That(indexStatistics.Single().ApproximateRecordCount, Is.EqualTo(12));
    foreach (var (dimension, value, count) in new[]
        { ("source", "US", 1), ("country", "US", 1), ("page", "/blog", 2), ("device", "Desktop", 1), ("device", "Mobile", 1) })
    {
      var query = Query(dimension, value);
      var indexed = engine.Query(query);
      query.Source = QuerySource.RawEvents;
      Assert.That(indexed.Totals.Values, Is.EqualTo(engine.Query(query).Totals.Values));
      Assert.That(indexed.Plan.UsedDimensionIndexes, Is.True);
      Assert.That(indexed.Totals[Metric.Events], Is.EqualTo(count));
    }
    Assert.That(engine.Query(Query("source", "US", "other-site")).Totals[Metric.Events], Is.EqualTo(1));
  }

  [Test]
  public void CombinedCopiesRollOverOnceAndRouteLateAndNewEventsAcrossReopen()
  {
    void Options(AnalyticsOptions options) { Configure(options); options.Storage.DimensionIndexes.RecordsPerPartition = 4; }
    using (var engine = TestEngine.Create(directory, Options))
    {
      View(engine, "a", Start.AddMinutes(1)); engine.Flush();
      Assert.That(engine.RollOverPartitions(Start.Ticks), Is.EqualTo(1), "One event contributes four copies to the shared partition target.");
      Assert.That(engine.RollOverPartitions(Start.Ticks), Is.Zero);
      View(engine, "late", Start.AddMinutes(2));
      View(engine, "new", Start.AddDays(3).AddMinutes(1)); engine.Flush();
      foreach (var dimension in Dimensions)
      {
        var index = engine.Storage.GetDimensionIndex(dimension);
        Assert.That(index.GetPartitions(), Has.Count.EqualTo(2));
        Assert.That(CountNamespace(index), Is.EqualTo(3));
      }
      var query = Query("country", "US");
      var indexed = engine.Query(query);
      query.Source = QuerySource.RawEvents;
      Assert.That(indexed.Totals.Values, Is.EqualTo(engine.Query(query).Totals.Values));
      Assert.That(indexed.Totals[Metric.Events], Is.EqualTo(3));
      engine.Compact();
    }
    using var reopened = TestEngine.Create(directory, Options);
    var result = reopened.Query(Query("source", "US"));
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(3));
  }

  [Test]
  public void RemovingOneDimensionRemovesItsNamespaceAndReenablingRestoresAllRetainedEvents()
  {
    using (var engine = TestEngine.Create(directory, Configure))
    { View(engine, "a", Start); engine.Flush(); }
    using (var reduced = TestEngine.Create(directory, options =>
    { Configure(options); options.Storage.DimensionIndexes.Dimensions.Remove("source"); }))
    {
      Assert.That(reduced.Storage.GetDimensionIndex("source"), Is.Null);
      Assert.That(reduced.DimensionIndexes.AllStores.Count(), Is.EqualTo(1));
      foreach (var dimension in Dimensions.Where(dimension => dimension != "source"))
        Assert.That(CountNamespace(reduced.Storage.GetDimensionIndex(dimension)), Is.EqualTo(1));
      var store = reduced.DimensionIndexes.Store;
      using (var lease = store.Acquire(store.ListPartitions().Single()))
      {
        using var iterator = lease.Tree.CreateIterator(); iterator.SeekFirst();
        while (iterator.Next())
          Assert.That(iterator.CurrentKey.Span.StartsWith(DimensionIndexKeyCodec.Namespace("source")), Is.False);
      }
      View(reduced, "b", Start.AddMinutes(1)); reduced.Flush();
    }
    using var restored = TestEngine.Create(directory, Configure);
    var result = restored.Query(Query("source", "US"));
    Assert.That(result.Plan.UsedDimensionIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(2));
  }

  [Test]
  public async Task ErasureAndRetentionRemoveCopiesFromEveryNamespaceAndKeepSitesIsolated()
  {
    using (var engine = TestEngine.Create(directory, Configure))
    {
      View(engine, "erase", Start);
      View(engine, "keep", Start.AddMinutes(1));
      View(engine, "erase", Start, site: "other-site");
      View(engine, "recent", DateTime.UtcNow.AddMinutes(-2)); engine.Flush();
      Assert.That(await engine.EraseVisitorAsync("erase"), Is.EqualTo(1));
      Assert.That(engine.Query(Query("source", "US", "other-site")).Totals[Metric.Events], Is.EqualTo(1));
      foreach (var dimension in Dimensions)
        Assert.That(CountNamespace(engine.Storage.GetDimensionIndex(dimension)), Is.EqualTo(3));
      engine.Compact();
    }
    using var retained = TestEngine.Create(directory, options =>
    { Configure(options); options.Retention.RawEvents = TimeSpan.FromDays(2); });
    retained.ApplyRetention();
    foreach (var dimension in Dimensions)
      Assert.That(CountNamespace(retained.Storage.GetDimensionIndex(dimension)), Is.EqualTo(1));
    var query = Query("source", "US"); query.Range = DateRange.LastDays(10);
    var indexed = retained.Query(query); query.Source = QuerySource.RawEvents;
    Assert.That(indexed.Totals.Values, Is.EqualTo(retained.Query(query).Totals.Values));
    Assert.That(indexed.Totals[Metric.Events], Is.EqualTo(1));
  }

  [Test]
  public void MissingSharedPartitionRebuildsAndCompactsEveryDimensionBeforeStartupCompletes()
  {
    string partitionDirectory;
    using (var engine = TestEngine.Create(directory, Configure))
    {
      View(engine, "a", Start); engine.Flush();
      var index = engine.Storage.GetDimensionIndex("source");
      partitionDirectory = Path.Combine(index.Directory, index.GetPartitions().Single());
    }
    Assert.That(Path.GetFullPath(partitionDirectory).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
    Directory.Delete(partitionDirectory, recursive: true);
    using var restored = TestEngine.Create(directory, Configure);
    foreach (var dimension in Dimensions)
      Assert.That(CountNamespace(restored.Storage.GetDimensionIndex(dimension)), Is.EqualTo(1));
    var store = restored.DimensionIndexes.Store;
    using var lease = store.Acquire(store.ListPartitions().Single());
    Assert.That(lease.Tree.Maintenance.MutableSegmentRecordCount, Is.Zero);
    Assert.That(lease.Tree.Maintenance.ReadOnlySegmentsRecordCount, Is.Zero);
    Assert.That(lease.Tree.Maintenance.TotalRecordCount, Is.EqualTo(4));
  }

  [Test]
  public void MissingValuesAndOverlappingDimensionNamesDoNotAliasNamespaces()
  {
    using var engine = TestEngine.Create(directory, options =>
    {
      options.Sessions.Enabled = false;
      options.Rollups.Definitions.Clear();
      options.Storage.DimensionIndexes.Dimensions.AddRange(["tag", "tag-extra"]);
    });
    Assert.That(engine.Track(new AnalyticsEvent("custom")
    { VisitorId = "empty", Timestamp = Utc(Start), Dimensions = { ["tag"] = "", ["tag-extra"] = "x" } }), Is.True);
    Assert.That(engine.Track(new AnalyticsEvent("custom")
    { VisitorId = "unicode", Timestamp = Utc(Start), Dimensions = { ["tag-extra"] = "γ😀" } }), Is.True);
    engine.Flush();
    Assert.That(CountNamespace(engine.Storage.GetDimensionIndex("tag")), Is.EqualTo(1));
    Assert.That(CountNamespace(engine.Storage.GetDimensionIndex("tag-extra")), Is.EqualTo(2));
    foreach (var (dimension, value) in new[] { ("tag", ""), ("tag-extra", "x"), ("tag-extra", "γ😀") })
    {
      var query = Query(dimension, value); query.Event = "custom";
      var indexed = engine.Query(query); query.Source = QuerySource.RawEvents;
      Assert.That(indexed.Plan.UsedDimensionIndexes, Is.True);
      Assert.That(indexed.Totals[Metric.Events], Is.EqualTo(1));
      Assert.That(indexed.Totals.Values, Is.EqualTo(engine.Query(query).Totals.Values));
    }
  }
}
