using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class SharedRollupIndexTests : DirectoryTest
{
  static DateTime Start => DateTime.UtcNow.Date.AddDays(-5);

  static void Configure(AnalyticsOptions options)
  {
    options.Sessions.Enabled = false;
    options.Ingestion.WorkerCount = 2;
    options.Queries.MaxDegreeOfParallelism = 2;
    DashboardAnalyticsOptions.ConfigureIndexes(options);
  }

  static void View(AnalyticsEngine engine, string visitor, DateTime at, string site = null, string page = "/blog") =>
      Assert.That(engine.Track(new AnalyticsEvent("pageview")
      {
        VisitorId = visitor, Site = site, Timestamp = Utc(at), Url = page, UserAgent = UserAgents.ChromeWindows,
        Dimensions = { ["source"] = "GitHub", ["country"] = "US" }, Values = { ["score"] = 3 }
      }), Is.True);

  static AnalyticsQuery Query(string dimension, string value, string group, string site = null) => new()
  {
    Site = site, Range = Range(Start, Start.AddDays(2)), Filters = { Filter.Equal(dimension, value) },
    GroupBy = { group }, Metrics = { Metric.Events, Metric.Visitors, Metric.Sum("score") }
  };

  static long CountNamespace(AnalyticsPartitionedStorage<Memory<byte>, RollupValue> storage)
  {
    long count = 0;
    foreach (var partition in storage.GetPartitions())
    {
      using var lease = storage.Acquire(partition);
      using var iterator = lease.ZoneTree.CreateIterator();
      iterator.Seek(storage.KeyPrefix.ToArray());
      while (iterator.Next() && iterator.CurrentKey.Span.StartsWith(storage.KeyPrefix.Span)) count++;
    }
    return count;
  }

  [Test]
  public void TwentyFourLogicalIndexesShareTwoStoresWithIsolatedKeyRangesAndStatistics()
  {
    using var engine = TestEngine.Create(directory, Configure);
    View(engine, "a", Start.AddMinutes(1));
    View(engine, "b", Start.AddMinutes(1), "other-site");
    engine.Flush();
    Assert.That(engine.RollupIndexes.Indexes, Has.Count.EqualTo(24));
    Assert.That(engine.RollupIndexes.AllStores.Count(), Is.EqualTo(2));
    Assert.That(Directory.EnumerateDirectories(Path.Combine(directory, "rollup-indexes")).Select(Path.GetFileName),
        Is.EquivalentTo(new[] { "hour", "day" }));
    foreach (var granularity in new[] { Granularity.Hour, Granularity.Day })
    {
      var physical = engine.RollupIndexes.Indexes.Where(index => index.Granularity == granularity).Select(index => index.Store).Distinct().Single();
      long logicalRows = 0;
      foreach (var definition in engine.Options.Storage.RollupIndexes.Definitions)
      {
        var storage = engine.Storage.GetRollupIndex(granularity, definition.Dimension,
            definition.Dimensions.Where(dimension => dimension != definition.Dimension).ToArray());
        Assert.That(storage.Directory, Is.EqualTo(physical.RootDirectory));
        Assert.That(storage.KeyPrefix.IsEmpty, Is.False);
        Assert.That(CountNamespace(storage), Is.EqualTo(2), "One row per site in this logical namespace.");
        logicalRows += CountNamespace(storage);
      }
      using var lease = physical.Acquire(physical.ListPartitions().Single());
      Assert.That(lease.Tree.Maintenance.TotalRecordCount, Is.EqualTo(logicalRows));
    }
    var partitions = engine.GetStatistics().Partitions.Where(partition => partition.Directory.StartsWith("rollup-indexes/", StringComparison.Ordinal)).ToArray();
    Assert.That(partitions, Has.Length.EqualTo(2));
    Assert.That(partitions.Select(partition => partition.Directory).Distinct().Count(), Is.EqualTo(2));
    foreach (var site in new[] { "default", "other-site" })
      foreach (var (dimension, value) in new[] { ("source", "GitHub"), ("country", "US"), ("device", "Desktop"), ("page", "/blog") })
        foreach (var group in new[] { "source", "country", "device", "page" }.Where(group => group != dimension))
        {
          var query = Query(dimension, value, group, site);
          var indexed = engine.Query(query);
          query.Source = QuerySource.RawEvents;
          var raw = engine.Query(query);
          Assert.That(indexed.Plan.UsedRollupIndexes, Is.True, $"{site}: {dimension} -> {group}");
          Assert.That(indexed.Totals.Values, Is.EqualTo(raw.Totals.Values));
          Assert.That(indexed.Totals[Metric.Events], Is.EqualTo(1));
          Assert.That(indexed.Rows.Select(row => string.Join('|', row.Dimensions)), Is.EqualTo(raw.Rows.Select(row => string.Join('|', row.Dimensions))));
        }
  }

  [Test]
  public void CombinedIndexRowsTriggerSharedPartitionRolloverAndKeepLateEventsReadable()
  {
    void Options(AnalyticsOptions options) { Configure(options); options.Storage.RollupIndexes.RecordsPerPartition = 4; }
    using (var engine = TestEngine.Create(directory, Options))
    {
      View(engine, "a", Start.AddMinutes(1)); engine.Flush();
      foreach (var store in engine.RollupIndexes.AllStores)
        Assert.That(store.RollOver(Start.AddDays(1).Ticks), Is.True, "Twelve logical rows share the four-row partition target.");
      View(engine, "late", Start.AddMinutes(2));
      View(engine, "new", Start.AddDays(1).AddMinutes(1)); engine.Flush();
      foreach (var granularity in new[] { Granularity.Hour, Granularity.Day })
      {
        var storage = engine.Storage.GetRollupIndex(granularity, "source", "country");
        Assert.That(storage.GetPartitions(), Has.Count.EqualTo(2));
        Assert.That(CountNamespace(storage), Is.EqualTo(2));
      }
      var query = Query("source", "GitHub", "country");
      var indexed = engine.Query(query);
      query.Source = QuerySource.RawEvents;
      Assert.That(indexed.Totals.Values, Is.EqualTo(engine.Query(query).Totals.Values));
      Assert.That(indexed.Totals[Metric.Events], Is.EqualTo(3));
      engine.Compact();
    }
    using var reopened = TestEngine.Create(directory, Options);
    var result = reopened.Query(Query("country", "US", "source"));
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(3));
  }

  [Test]
  public void RemovingOneNamespaceRebuildsSharedCopiesWithoutRemovingOtherDirections()
  {
    using (var engine = TestEngine.Create(directory, Configure))
    { View(engine, "a", Start.AddMinutes(1)); engine.Flush(); }
    using (var reduced = TestEngine.Create(directory, options =>
    {
      Configure(options);
      options.Storage.RollupIndexes.Definitions.RemoveAll(index => index.Dimension == "source" && index.Definition.Id == "country,source");
    }))
    {
      Assert.That(reduced.Storage.GetRollupIndex(Granularity.Day, "source", "country"), Is.Null);
      Assert.That(reduced.RollupIndexes.AllStores.Count(), Is.EqualTo(2));
      var reverse = reduced.Storage.GetRollupIndex(Granularity.Day, "country", "source");
      Assert.That(CountNamespace(reverse), Is.EqualTo(1));
      foreach (var partition in reverse.GetPartitions())
      {
        using var lease = reverse.Acquire(partition);
        using var iterator = lease.ZoneTree.CreateIterator(); iterator.SeekFirst();
        while (iterator.Next())
          Assert.That(iterator.CurrentKey.Span.StartsWith(RollupIndexKeyCodec.Namespace("source", "country,source")), Is.False);
      }
      Assert.That(reduced.Query(Query("country", "US", "source")).Totals[Metric.Events], Is.EqualTo(1));
      View(reduced, "b", Start.AddMinutes(2)); reduced.Flush();
    }
    using var restored = TestEngine.Create(directory, Configure);
    var result = restored.Query(Query("source", "GitHub", "country"));
    Assert.That(result.Plan.UsedRollupIndexes, Is.True);
    Assert.That(result.Totals[Metric.Events], Is.EqualTo(2));
  }

  [Test]
  public void MissingSharedPartitionRebuildsEveryLogicalIndexFromAuthoritativeRollups()
  {
    string partitionDirectory;
    using (var engine = TestEngine.Create(directory, Configure))
    {
      View(engine, "a", Start.AddMinutes(1)); engine.Flush();
      var storage = engine.Storage.GetRollupIndex(Granularity.Day, "source", "country");
      partitionDirectory = Path.Combine(storage.Directory, storage.GetPartitions().Single());
    }
    Assert.That(Path.GetFullPath(partitionDirectory).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
    Directory.Delete(partitionDirectory, recursive: true);
    using var restored = TestEngine.Create(directory, Configure);
    foreach (var definition in restored.Options.Storage.RollupIndexes.Definitions)
    {
      var group = definition.Dimensions.Single(dimension => dimension != definition.Dimension);
      var value = definition.Dimension switch { "source" => "GitHub", "country" => "US", "device" => "Desktop", _ => "/blog" };
      var result = restored.Query(Query(definition.Dimension, value, group));
      Assert.That(result.Plan.UsedRollupIndexes, Is.True);
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(1));
    }
    AssertAllCopiesOnDisk(restored);
  }

  [Test]
  public void BackfilledCopiesAreCompactedBeforeStartupCompletes()
  {
    using (var engine = TestEngine.Create(directory, options => options.Sessions.Enabled = false))
    { View(engine, "a", Start.AddMinutes(1)); engine.Flush(); }
    using var indexed = TestEngine.Create(directory, Configure);
    AssertAllCopiesOnDisk(indexed);
    Assert.That(indexed.Query(Query("source", "GitHub", "country")).Totals[Metric.Events], Is.EqualTo(1));
  }

  static void AssertAllCopiesOnDisk(AnalyticsEngine engine)
  {
    foreach (var store in engine.RollupIndexes.AllStores)
      foreach (var partition in store.ListPartitions())
      {
        using var lease = store.Acquire(partition);
        Assert.That(lease.Tree.Maintenance.MutableSegmentRecordCount, Is.Zero);
        Assert.That(lease.Tree.Maintenance.ReadOnlySegmentsRecordCount, Is.Zero);
        Assert.That(lease.Tree.Maintenance.TotalRecordCount, Is.GreaterThan(0));
      }
  }
}
