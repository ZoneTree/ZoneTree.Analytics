namespace ZoneTree.Analytics.UnitTests;

public sealed class QueryRefactoringTests : DirectoryTest
{
  [TestCase(false, Granularity.Hour)]
  [TestCase(false, Granularity.Hour | Granularity.Day)]
  [TestCase(true, Granularity.Hour | Granularity.Day)]
  public void PartialHoursAreNotLostWhenDailyRollupsAreEnabled(bool raw, Granularity granularities)
  {
    var day = DateTime.UtcNow.Date.AddDays(-2);
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.StoreRawEvents = raw;
      o.Rollups.Granularities = granularities;
    });
    foreach (var hour in new[] { 0, 1, 2 })
      engine.Track(new AnalyticsEvent("signup") { Timestamp = Utc(day.AddHours(hour).AddMinutes(30)) });
    engine.Flush();
    var range = Range(day.AddMinutes(15), day.AddHours(2).AddMinutes(45));
    var result = engine.Query(new AnalyticsQuery { Event = "signup", Range = range, Metrics = { Metric.Events } });
    Assert.Multiple(() =>
    {
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(3));
      Assert.That(result.Plan.IsComplete, Is.EqualTo(raw));
      Assert.That(engine.GetEventNames(range), Does.Contain("signup"));
    });
  }

  [TestCase(1)]
  [TestCase(4)]
  public void RawSessionReplayIncludesTimeoutBoundaryEventsAndKeepsEventCountsInRange(int parallelism)
  {
    var day = DateTime.UtcNow.Date.AddDays(-2);
    using var engine = TestEngine.Create(directory, o => o.Queries.MaxDegreeOfParallelism = parallelism);
    engine.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/start", Timestamp = Utc(day) });
    for (var minute = 30; minute < 240; minute += 30)
    {
      engine.Track(new AnalyticsEvent("activity") { VisitorId = "alice", Timestamp = Utc(day.AddMinutes(minute)) });
      engine.Track(new AnalyticsEvent("pageview") { VisitorId = "other", Url = "/other", Timestamp = Utc(day.AddMinutes(minute + 1)) });
    }
    engine.Track(new AnalyticsEvent("pageview") { VisitorId = "alice", Url = "/end", Timestamp = Utc(day.AddHours(4)) });
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery
    {
      Range = Range(day, day.AddMinutes(10)), Source = QuerySource.RawEvents,
      Metrics = { Metric.PageViews, Metric.Sessions, Metric.SessionDuration, Metric.PageViewsPerSession, Metric.Exits }
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(1));
      Assert.That(result.Totals[Metric.Sessions], Is.EqualTo(1));
      Assert.That(result.Totals[Metric.SessionDuration], Is.EqualTo(TimeSpan.FromHours(4).TotalSeconds));
      Assert.That(result.Totals[Metric.PageViewsPerSession], Is.EqualTo(2));
      Assert.That(result.Totals[Metric.Exits], Is.Zero);
    });
  }
}
