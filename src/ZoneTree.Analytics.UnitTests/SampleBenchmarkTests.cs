namespace ZoneTree.Analytics.UnitTests;

public sealed class SampleBenchmarkTests : DirectoryTest
{
  [TestCase(false)]
  [TestCase(true)]
  public void PreparationPreservesTheRealisticDataShape(bool percentiles)
  {
    var end = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    var request = new SampleBenchmarkRequest(40, 7, 4, percentiles);
    var events = SampleBenchmark.PrepareEvents(request, TimeZoneInfo.Utc, _ => { }, end);
    var expected = SampleTraffic.CreateEvents(40, 7, timeZone: TimeZoneInfo.Utc, endingAt: end).ToList();
    Assert.That(events, Has.Length.EqualTo(40));
    for (var index = 0; index < events.Length; index++)
    {
      Assert.That(events[index].Name, Is.EqualTo(expected[index].Name));
      Assert.That(events[index].Timestamp, Is.EqualTo(expected[index].Timestamp));
      Assert.That(events[index].Url, Is.EqualTo(expected[index].Url));
      Assert.That(events[index].Referrer, Is.EqualTo(expected[index].Referrer));
      Assert.That(events[index].Values, Is.EquivalentTo(expected[index].Values));
    }
  }

  [TestCase(1, false, true)]
  [TestCase(4, true, false)]
  public void BenchmarkUsesDefaultPerWorkerLimitsAndOptionalPercentilesAndIndexes(int workers, bool percentiles, bool indexes)
  {
    var options = DashboardAnalyticsOptions.CreateBenchmark(directory, TimeZoneInfo.Utc, workers, percentiles, indexes);
    var defaults = new AnalyticsOptions();
    Assert.That(options.Ingestion.WorkerCount, Is.EqualTo(workers));
    Assert.That(options.Ingestion.QueueCapacity, Is.EqualTo(defaults.Ingestion.QueueCapacity));
    Assert.That(options.Ingestion.MaxBufferedRollupRows, Is.EqualTo(defaults.Ingestion.MaxBufferedRollupRows));
    Assert.That(options.Sessions.MaxCachedSessions, Is.EqualTo(defaults.Sessions.MaxCachedSessions));
    Assert.That(options.Ingestion.QueueFullBehavior, Is.EqualTo(QueueFullBehavior.Wait));
    Assert.That(options.Rollups.ValueAggregates.ContainsKey("load_time"), Is.EqualTo(percentiles));
    Assert.That(options.Storage.DimensionIndexes.Dimensions, indexes ? Is.Not.Empty : Is.Empty);
    Assert.That(options.Storage.RollupIndexes.Definitions, indexes ? Is.Not.Empty : Is.Empty);
  }

  // A provider of a local database with a /8 per sample country, except the skipped ones: with a
  // region and a city in each, or only countries.
  IpLocationProvider SampleCountries(bool cities = true, params string[] skipped)
  {
    var writer = new MmdbWriter(6);
    var block = 1;
    foreach (var country in SampleTraffic.CountryCodes)
      if (!skipped.Contains(country))
        writer.Insert($"{block++}.0.0.0/8", cities
            ? writer.SapicsCity(country, $"{country} region", $"{country} city")
            : writer.CountryCode(country));
    var path = Path.Combine(directory, "geo", cities ? "cities.mmdb" : "countries.mmdb");
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    File.WriteAllBytes(path, writer.ToArray());
    var options = new IpLocationOptions();
    options.Sources.Add(new(null, path));
    var provider = new IpLocationProvider(options);
    provider.WaitForDatabaseAsync().Wait(TimeSpan.FromSeconds(10));
    return provider;
  }

  [Test]
  public async Task GeolocatedVisitorsCarryAddressesOfTheirCountries()
  {
    var end = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    var request = new SampleBenchmarkRequest(3000, 7, 4);
    await using var database = SampleCountries(skipped: "KE");
    var plain = SampleBenchmark.PrepareEvents(request, TimeZoneInfo.Utc, _ => { }, end);
    var located = SampleBenchmark.PrepareEvents(request, TimeZoneInfo.Utc, _ => { }, end,
        addresses: SampleTraffic.FindAddresses(database));
    var countries = new HashSet<string>();
    for (var index = 0; index < plain.Length; index++)
    {
      // Visitor ids differ by a random batch id; the visitor numbers match.
      Assert.That(located[index].VisitorId?.Split('-')[^1], Is.EqualTo(plain[index].VisitorId?.Split('-')[^1]));
      plain[index].Dimensions.TryGetValue(StandardDimensions.Country, out var country);
      if (country == null)
      {
        Assert.That(located[index].IpAddress, Is.EqualTo(plain[index].IpAddress), "Bots keep their addresses");
        continue;
      }
      countries.Add(country);
      if (country == "KE")
      {
        // Countries the database lacks keep their dimension.
        Assert.That(located[index].Dimensions[StandardDimensions.Country], Is.EqualTo("KE"));
        continue;
      }
      Assert.That(located[index].Dimensions.ContainsKey(StandardDimensions.Country), Is.False);
      Assert.That(database.Locate(located[index].IpAddress)?.Country, Is.EqualTo(country));
    }
    Assert.That(countries, Has.Count.GreaterThan(40));
  }

  [Test]
  public async Task GeolocatedBenchmarksReportTheSameCountriesAsDimensionsAndCitiesOnRequest()
  {
    await using var database = SampleCountries();
    await using var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc, ipLocation: database);
    Assert.That(benchmark.AvailableGeoPrecision, Is.EqualTo(GeoPrecision.City));
    async Task<(Dictionary<string, long> Countries, Dictionary<string, long> Cities)> Run(bool geolocation, bool cities)
    {
      var result = await benchmark.RunAsync(new(400, 7, 2, Geolocation: geolocation, Cities: cities), _ => { },
          CancellationToken.None, CancellationToken.None);
      Assert.That(result.Geolocation, Is.EqualTo(geolocation));
      Assert.That(result.Cities, Is.EqualTo(cities));
      using var lease = benchmark.Reports.Acquire();
      Dictionary<string, long> Breakdown(string dimension) => lease.Engine.Web
          .GetBreakdown(new() { Site = BenchmarkReportStore.Site, Range = DateRange.LastDays(8), Limit = 100 }, dimension)
          .Rows.Where(row => !string.IsNullOrEmpty(row.Value)).ToDictionary(row => row.Value, row => row.Visitors);
      return (Breakdown(StandardDimensions.Country), Breakdown(StandardDimensions.City));
    }
    var dimensions = await Run(false, false);
    var countries = await Run(true, false);
    var cities = await Run(true, true);
    Assert.Multiple(() =>
    {
      Assert.That(dimensions.Countries, Has.Count.GreaterThan(10));
      Assert.That(countries.Countries, Is.EquivalentTo(dimensions.Countries));
      Assert.That(cities.Countries, Is.EquivalentTo(dimensions.Countries));
      // A country run keeps no cities, although the database has them.
      Assert.That(countries.Cities, Is.Empty);
      Assert.That(cities.Cities, Is.EquivalentTo(dimensions.Countries.ToDictionary(country => $"{country.Key} city", country => country.Value)));
    });
  }

  [Test]
  public async Task GeolocationNeedsADatabaseAndCitiesADatabaseWithCities()
  {
    var options = DashboardAnalyticsOptions.CreateBenchmark(directory, TimeZoneInfo.Utc, 1, false, true);
    Assert.That(options.Web.GeoLocator, Is.Null);
    await using (var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc))
    {
      Assert.That(benchmark.AvailableGeoPrecision, Is.EqualTo(GeoPrecision.None));
      Assert.ThrowsAsync<InvalidOperationException>(() =>
          benchmark.RunAsync(new(17, 7, 1, Geolocation: true), _ => { }, CancellationToken.None, CancellationToken.None));
    }
    await using var countries = SampleCountries(cities: false);
    await using var countryBenchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc, ipLocation: countries);
    Assert.That(countryBenchmark.AvailableGeoPrecision, Is.EqualTo(GeoPrecision.Country));
    Assert.ThrowsAsync<InvalidOperationException>(() =>
        countryBenchmark.RunAsync(new(17, 7, 1, Geolocation: true, Cities: true), _ => { }, CancellationToken.None, CancellationToken.None));
  }

  [Test]
  public async Task ABenchmarkWithoutIndexesRetainsBrowsableReportsWithoutIndexes()
  {
    await using var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc);
    var result = await benchmark.RunAsync(new(17, 7, 2, Indexes: false), _ => { },
        CancellationToken.None, CancellationToken.None);
    Assert.That(result.Indexes, Is.False);
    Assert.That(result.Ingested, Is.EqualTo(17));
    using var lease = benchmark.Reports.Acquire();
    var report = lease.Engine.Web.GetSummary(new() { Site = BenchmarkReportStore.Site, Range = DateRange.LastDays(8) });
    Assert.That(report.Summary.PageViews, Is.GreaterThan(0));
    Assert.That(lease.Engine.RollupIndexes.AllStores, Is.Empty, "reopened reports build no indexes");
  }

  [Test]
  public async Task SmallFixtureFlushesAllInputsAndRetainsBrowsableReports()
  {
    Directory.CreateDirectory(directory);
    var sentinel = Path.Combine(directory, "existing-data.txt");
    await File.WriteAllTextAsync(sentinel, "preserve");
    await using var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc);
    var phases = new List<string>();
    var result = await benchmark.RunAsync(new(17, 7, 2), update => phases.Add(update.State),
        CancellationToken.None, CancellationToken.None);
    Assert.That(result, Is.Not.Null);
    Assert.That(result.Ingested, Is.EqualTo(17));
    Assert.That(result.Errors, Is.Zero);
    Assert.That(result.Dropped, Is.Zero);
    Assert.That(result.Rejected, Is.Zero);
    Assert.That(result.IngestionMs, Is.GreaterThanOrEqualTo(result.SubmissionMs));
    Assert.That(phases, Does.Contain("preparing").And.Contain("running").And.Contain("flushing").And.Contain("compacting"));
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "data/ingestion-benchmarks")), Has.Length.EqualTo(1));
    using (var lease = benchmark.Reports.Acquire())
    {
      var report = lease.Engine.Web.GetSummary(new() { Site = BenchmarkReportStore.Site, Range = DateRange.LastDays(8) });
      Assert.That(report.Summary.PageViews, Is.GreaterThan(0));
      Assert.That(lease.Engine.RollupIndexes.AllStores.Count(), Is.EqualTo(2));
      foreach (var index in lease.Engine.RollupIndexes.AllStores)
        foreach (var partition in index.ListPartitions())
        {
          using var indexLease = index.Acquire(partition);
          Assert.That(indexLease.Tree.Maintenance.MutableSegmentRecordCount, Is.Zero);
          Assert.That(indexLease.Tree.Maintenance.ReadOnlySegmentsRecordCount, Is.Zero);
        }
    }
    await benchmark.Reports.ClearAsync(CancellationToken.None);
    Assert.That(benchmark.Reports.Result, Is.Null);
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "data/ingestion-benchmarks")), Is.Empty);
    Assert.That(await File.ReadAllTextAsync(sentinel), Is.EqualTo("preserve"));
  }

  [Test]
  public async Task CancellationDrainsAndCleansTheTemporaryStore()
  {
    await using var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc);
    using var cancellation = new CancellationTokenSource();
    var result = await benchmark.RunAsync(new(25, 1, 1), update =>
    {
      if (update.State is "ingesting" or "flushing") cancellation.Cancel();
    }, cancellation.Token, CancellationToken.None);
    Assert.That(result, Is.Null);
    Assert.That(Directory.GetDirectories(Path.Combine(directory, "data/ingestion-benchmarks")), Is.Empty);
  }

  [Test]
  public async Task SuccessfulRunsReplaceTheRetainedDatasetAndSurviveRestart()
  {
    await using (var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc))
    {
      await benchmark.RunAsync(new(17, 7, 2), _ => { }, CancellationToken.None, CancellationToken.None);
      var updates = new List<SampleBenchmarkProgress>();
      await benchmark.RunAsync(new(25, 7, 2), updates.Add, CancellationToken.None, CancellationToken.None);
      Assert.That(updates.Any(update => update.Ingested == 25), Is.True);
      Assert.That(updates.All(update => update.Ingested <= update.Submitted), Is.True);
      Assert.That(Directory.GetDirectories(Path.Combine(directory, "data/ingestion-benchmarks")), Has.Length.EqualTo(1));
    }
    await using var reopened = new SampleBenchmark(directory, TimeZoneInfo.Utc);
    Assert.That(reopened.Reports.Result.Ingested, Is.EqualTo(25));
    using var lease = reopened.Reports.Acquire();
    Assert.That(lease.Engine.Web.GetSummary(new() { Site = BenchmarkReportStore.Site }).Summary.PageViews, Is.GreaterThan(0));
  }

  [Test]
  public async Task PublishingWaitsForPreviousReportReaders()
  {
    await using var benchmark = new SampleBenchmark(directory, TimeZoneInfo.Utc);
    await benchmark.RunAsync(new(17, 7, 2), _ => { }, CancellationToken.None, CancellationToken.None);
    using var previous = benchmark.Reports.Acquire();
    var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var next = benchmark.RunAsync(new(25, 7, 2), update =>
    {
      if (update.State == "publishing") publishing.TrySetResult();
    }, CancellationToken.None, CancellationToken.None);
    await publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.That(next.IsCompleted, Is.False);
    Assert.That(previous.Engine.Web.GetSummary(new() { Site = BenchmarkReportStore.Site }).Summary.PageViews, Is.GreaterThan(0));
    previous.Dispose();
    Assert.That((await next).Ingested, Is.EqualTo(25));
  }
}
