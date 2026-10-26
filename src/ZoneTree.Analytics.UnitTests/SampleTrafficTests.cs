using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class SampleTrafficTests
{
  static readonly DateTimeOffset End = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

  [TestCase(1, 1)]
  [TestCase(17, 7)]
  [TestCase(10000, 60)]
  [TestCase(10000, 90)]
  public void EventsMatchRequestedCountAndStayInRange(int count, int days)
  {
    var events = SampleTraffic.CreateEvents(count, days, "samples", TimeZoneInfo.Utc, End).ToList();
    Assert.That(events, Has.Count.EqualTo(count));
    Assert.That(events.All(item => item.Site == "samples" && item.Timestamp >= End.AddDays(-days) && item.Timestamp <= End), Is.True);
    Assert.That(events.All(item => item.Name is "pageview" or "signup" or "purchase"), Is.True);
  }

  [Test]
  public void TrafficHasDaytimePeaksWeekendsGrowthAndCampaigns()
  {
    var start = End.AddDays(-60);
    var profile = new SampleTrafficProfile(start, End, TimeZoneInfo.Utc);
    var daily = new int[60];
    var daytime = 0;
    var nighttime = 0;
    var weekday = 0;
    var weekend = 0;
    for (var index = 0; index < 250000; index++)
    {
      var time = profile.At((index + 0.5) / 250000).Timestamp;
      daily[(int)(time - start).TotalDays]++;
      if (time.Hour is >= 9 and < 18) daytime++;
      if (time.Hour is >= 0 and < 6) nighttime++;
      if (time.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) weekend++;
      else weekday++;
    }
    var weekdayCount = Enumerable.Range(0, 60).Count(day => start.AddDays(day).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
    Assert.That(daytime / 9.0, Is.GreaterThan(nighttime / 6.0 * 3), "Quiet nights and busy daytime hours");
    Assert.That(weekday / (double)weekdayCount, Is.GreaterThan(weekend / (double)(60 - weekdayCount) * 1.2), "Quieter weekends");
    Assert.That(daily.Max(), Is.GreaterThan(daily.Min() * 4), "Clear peaks and dips rather than a flat line");
    Assert.That(daily.Skip(50).Sum(), Is.GreaterThan(daily.Take(10).Sum() * 1.4), "Traffic grows over the period");
    TestContext.Out.WriteLine($"Daily traffic: {string.Join(',', daily)}");
  }

  [Test]
  public void VisitsVaryAndCampaignsChangeConversionsAndSources()
  {
    var events = SampleTraffic.CreateEvents(40000, 60, "samples", TimeZoneInfo.Utc, End).ToList();
    var humans = events.Where(item => item.VisitorId != null).ToList();
    var visits = humans.GroupBy(item => item.VisitorId).Select(group => group.ToList()).ToList();
    Assert.That(visits.Count(visit => visit.Count == 1), Is.GreaterThan(visits.Count * 0.15), "Single-page bounces");
    Assert.That(visits.Count(visit => visit.Count > 3), Is.GreaterThan(visits.Count * 0.15), "Longer visits");
    Assert.That(events.Any(item => item.VisitorId == null), Is.True, "Bot traffic");
    // Samples carry their country, since the sample and benchmark engines have no geolocator.
    var countries = humans.GroupBy(item => item.VisitorId)
        .Select(visitor => visitor.Select(item => item.Dimensions[StandardDimensions.Country]).Distinct().Single())
        .CountBy(country => country).ToDictionary();
    Assert.That(countries, Has.Count.GreaterThan(40), "Many countries, each visitor in one");
    Assert.That(countries.MaxBy(country => country.Value).Key, Is.EqualTo("US"));
    Assert.That(countries["US"] / (double)countries.Values.Sum(), Is.InRange(0.24, 0.32), "A realistic share for the largest country");
    Assert.That(countries["DE"], Is.GreaterThan(countries["NL"]));
    Assert.That(humans.Any(item => item.Name == "signup"), Is.True);
    Assert.That(humans.Any(item => item.Name == "purchase" && item.Values["revenue"] > 0), Is.True);
    foreach (var visit in visits)
      Assert.That(visit.Select(item => item.Timestamp), Is.Ordered, "Each visitor's events stay chronological");
    var campaign = humans.Where(item => item.Timestamp >= End.AddDays(-42) && item.Timestamp < End.AddDays(-37)).ToList();
    var baseline = humans.Where(item => item.Timestamp >= End.AddDays(-57) && item.Timestamp < End.AddDays(-51)).ToList();
    Assert.That(campaign.Count(item => item.Referrer == "https://github.com/") / (double)campaign.Count,
        Is.GreaterThan(baseline.Count(item => item.Referrer == "https://github.com/") / (double)baseline.Count * 2));
    Assert.That(campaign.Count(item => item.Name == "signup") / (double)campaign.Count,
        Is.GreaterThan(baseline.Count(item => item.Name == "signup") / (double)baseline.Count * 1.3));
  }

  [Test]
  public void ProfileUsesTheReportingTimeZoneAndLargeBatchesRemainLazy()
  {
    var zone = TimeZoneInfo.CreateCustomTimeZone("Sample+6", TimeSpan.FromHours(6), "Sample+6", "Sample+6");
    var profile = new SampleTrafficProfile(End.AddDays(-7), End, zone);
    var daytime = 0;
    var nighttime = 0;
    for (var index = 0; index < 10000; index++)
    {
      var local = TimeZoneInfo.ConvertTime(profile.At((index + 0.5) / 10000).Timestamp, zone);
      if (local.Hour is >= 9 and < 18) daytime++;
      if (local.Hour is >= 0 and < 6) nighttime++;
    }
    Assert.That(daytime / 9.0, Is.GreaterThan(nighttime / 6.0 * 3));
    Assert.That(SampleTraffic.CreateEvents(10000000, 90, endingAt: End).Take(100).Count(), Is.EqualTo(100));
  }
}

public sealed class SampleTrafficIngestionTests : DirectoryTest
{
  [TestCase(1)]
  [TestCase(4)]
  public async Task GeneratedTrafficProducesRealSessionsGoalsAndNonFlatReports(int workers)
  {
    await using var engine = TestEngine.Create(directory, options =>
    {
      options.Web.Bots = BotTraffic.TrackSeparately;
      options.Ingestion.WorkerCount = workers;
      options.Ingestion.QueueCapacity = 64;
    });
    var progress = new List<int>();
    await SampleTraffic.GenerateAsync(engine, 10003, 14, "samples", progress.Add, workers: workers);
    var request = new WebReportRequest { Site = "samples", Range = DateRange.LastDays(15) };
    var summary = engine.Web.GetSummary(request).Summary;
    Assert.That(progress, Is.Ordered);
    Assert.That(progress[^1], Is.EqualTo(10003));
    var statistics = engine.GetStatistics();
    Assert.That(statistics.EventsAccepted, Is.EqualTo(10003));
    Assert.That(statistics.EventsProcessed, Is.EqualTo(10003));
    Assert.That(statistics.Errors, Is.Zero);
    Assert.That(statistics.EventsDropped, Is.Zero);
    Assert.That(summary.Visitors, Is.GreaterThan(0));
    Assert.That(summary.BotHits, Is.GreaterThan(0));
    Assert.That(summary.BounceRate, Is.InRange(0.15, 0.65));
    Assert.That(summary.AverageSessionDuration, Is.GreaterThan(TimeSpan.FromSeconds(10)));
    var goals = engine.Web.GetGoals(request).Rows;
    Assert.That(goals.Select(goal => goal.Name), Does.Contain("signup").And.Contain("purchase"));
    TestContext.Out.WriteLine($"Generated reports: {summary.Visitors} visitors, {summary.PageViews} page views, {summary.BounceRate:P1} bounce rate, {summary.AverageSessionDuration} average duration.");
  }

  [Test]
  public async Task ParallelCancellationDrainsExactlyTheAcceptedEvents()
  {
    await using var engine = TestEngine.Create(directory, options =>
    {
      options.Web.Bots = BotTraffic.TrackSeparately;
      options.Ingestion.WorkerCount = 4;
      options.Ingestion.QueueCapacity = 16;
    });
    using var cancellation = new CancellationTokenSource();
    var processed = 0;
    async Task Generate() => await SampleTraffic.GenerateAsync(engine, 1000000, 60, "samples", count =>
    {
      processed = count;
      if (count >= 1000) cancellation.Cancel();
    }, workers: 4, cancellationToken: cancellation.Token);
    Assert.That(Generate, Throws.InstanceOf<OperationCanceledException>());
    await engine.FlushAsync();
    var statistics = engine.GetStatistics();
    Assert.That(processed, Is.InRange(1000, 999999));
    Assert.That(statistics.EventsAccepted, Is.EqualTo(processed));
    Assert.That(statistics.EventsProcessed, Is.EqualTo(processed));
    Assert.That(statistics.Errors, Is.Zero);
  }

  [Test]
  public async Task MoreWorkersThanEventsStillGenerateTheExactCount()
  {
    await using var engine = TestEngine.Create(directory, options => options.Web.Bots = BotTraffic.TrackSeparately);
    await SampleTraffic.GenerateAsync(engine, 3, workers: 64);
    Assert.That(engine.GetStatistics().EventsProcessed, Is.EqualTo(3));
  }
}
