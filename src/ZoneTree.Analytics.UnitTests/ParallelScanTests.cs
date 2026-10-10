using System.Globalization;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Parallel raw scans split ranges into time slices and merge the slices. Their results must
/// equal those of a sequential scan, also for sessions and funnels that span many slices.
/// </summary>
public sealed class ParallelScanTests
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-5);

  static readonly string[] Pages = ["/", "/pricing", "/docs", "/blog", "/signup"];

  static readonly (string Ip, string UserAgent)[] Devices =
  [
    ("10.0.0.1", UserAgents.ChromeWindows),
    ("10.0.0.2", UserAgents.SafariIPhone),
    ("10.0.0.3", UserAgents.SafariIPhone),
  ];

  string directory;

  [SetUp]
  public void SetUp() => directory = TestEngine.NewDirectory("parallel");

  [TearDown]
  public void TearDown() => TestEngine.Delete(directory);

  static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  /// <summary>
  /// Two days of sessions with 1 to 8 events, minutes apart, so that many sessions cross the
  /// boundaries of slices that are an hour or a few hours long.
  /// </summary>
  static List<AnalyticsEvent> Traffic()
  {
    var random = new Random(42);
    var events = new List<AnalyticsEvent>();
    for (var visitor = 0; visitor < 150; ++visitor)
    {
      var (ip, userAgent) = Devices[visitor % Devices.Length];
      var start = Day0.AddMinutes(random.Next(0, 60 * 40));
      for (var session = random.Next(1, 4); session > 0; --session)
      {
        var time = start;
        for (var i = random.Next(1, 9); i > 0; --i)
        {
          var page = Pages[random.Next(Pages.Length)];
          events.Add(new AnalyticsEvent(StandardEvents.PageView)
          {
            Url = "https://mysite.com" + page,
            VisitorId = "v" + visitor.ToString(CultureInfo.InvariantCulture),
            IpAddress = ip,
            UserAgent = userAgent,
            Timestamp = Utc(time)
          });
          if (page == "/signup")
          {
            events.Add(new AnalyticsEvent("signup")
            {
              VisitorId = "v" + visitor.ToString(CultureInfo.InvariantCulture),
              IpAddress = ip,
              UserAgent = userAgent,
              Timestamp = Utc(time.AddSeconds(30)),
              Metrics = { ["amount"] = random.Next(1, 100) / 4.0 }
            });
          }
          time = time.AddMinutes(random.Next(1, 13));
        }
        start = time.AddHours(2);
      }
    }
    return [.. events.OrderBy(x => x.Timestamp)];
  }

  static AnalyticsQuery[] Queries()
  {
    var range = DateRange.Between(Utc(Day0), Utc(Day0.AddDays(3)));
    AnalyticsQuery Raw(Action<AnalyticsQuery> configure)
    {
      var query = new AnalyticsQuery { Range = range, Source = QuerySource.RawEvents };
      configure(query);
      return query;
    }
    return
    [
      Raw(q =>
      {
        q.Interval = TimeInterval.Hour;
        q.Metrics.AddRange([
          Metric.PageViews, Metric.Visitors, Metric.Sessions, Metric.Bounces, Metric.BounceRate,
          Metric.SessionDuration, Metric.PageViewsPerSession, Metric.Entrances, Metric.Exits,
          Metric.EventsOf("signup"), Metric.Sum("amount", "signup"), Metric.Max("amount", "signup")]);
      }),
      Raw(q =>
      {
        q.Event = "signup";
        q.Interval = TimeInterval.Hours(6);
        q.Metrics.AddRange([
          Metric.Percentile("amount", 90), Metric.Median("amount"), Metric.First("amount"), Metric.Last("amount"),
          Metric.StandardDeviation("amount"), Metric.Distinct(StandardDimensions.Country),
          Metric.Cumulative(Metric.Sum("amount")), Metric.Increase(Metric.Last("amount"))]);
      }),
      Raw(q =>
      {
        q.GroupBy.Add(StandardDimensions.Page);
        q.Metrics.AddRange([Metric.PageViews, Metric.Visitors, Metric.Entrances, Metric.Exits, Metric.ExitRate]);
      }),
      Raw(q =>
      {
        q.GroupBy.Add(StandardDimensions.Country);
        q.Filters.Add(Filter.Equal(StandardDimensions.Browser, "Safari"));
        q.Metrics.AddRange([Metric.Sessions, Metric.BounceRate, Metric.AverageSessionDuration, Metric.Visitors]);
      }),
      Raw(q =>
      {
        q.Interval = TimeInterval.Day;
        q.Segments.Add(Segment.VisitorsWhoViewed("/pricing"));
        q.Metrics.AddRange([Metric.PageViews, Metric.Visitors, Metric.Sessions, Metric.Exits]);
      }),
      Raw(q =>
      {
        q.Segments.Add(Segment.SessionsWith("signup").Exclude());
        q.Metrics.AddRange([Metric.Sessions, Metric.BounceRate, Metric.PageViews]);
      }),
    ];
  }

  static FunnelQuery[] Funnels()
  {
    FunnelQuery Funnel(TimeSpan? window)
    {
      var funnel = new FunnelQuery { Range = DateRange.Between(Utc(Day0), Utc(Day0.AddDays(3))), Window = window };
      funnel.Steps.Add(FunnelStep.Page("Home", "/"));
      funnel.Steps.Add(FunnelStep.Page("Pricing", "/pricing"));
      funnel.Steps.Add(FunnelStep.Goal("signup"));
      return funnel;
    }
    var segmented = Funnel(TimeSpan.FromHours(3));
    segmented.Segments.Add(Segment.VisitorsWhoViewed("/docs"));
    return [Funnel(null), Funnel(TimeSpan.FromMinutes(20)), segmented];
  }

  sealed record Results(string[] Queries, string[] Funnels, long CurrentVisitors);

  Results Run(int maxDegreeOfParallelism, IEnumerable<AnalyticsEvent> track = null)
  {
    using var engine = TestEngine.Create(
        directory, options => options.Queries.MaxDegreeOfParallelism = maxDegreeOfParallelism);
    if (track != null)
    {
      foreach (var e in track)
        engine.Track(e);
      engine.Flush();
    }
    var queries = Queries().Select(x => Describe(engine.Query(x))).ToArray();
    var funnels = Funnels()
        .Select(x => string.Join(" ", engine.QueryAsync(x).Result.Steps.Select(s => s.Visitors)))
        .ToArray();
    return new Results(queries, funnels, engine.Web.GetCurrentVisitors(window: TimeSpan.FromMinutes(30)).Value);
  }

  /// <summary>
  /// Rows sorted by their dimensions, values rounded to absorb the summation order of doubles.
  /// </summary>
  static string Describe(AnalyticsResult result)
  {
    Assert.That(result.Plan.UsedRawEvents, Is.True);
    static string Row(AnalyticsRow row) =>
        string.Join("|", row.Dimensions) + " = " +
        string.Join(", ", row.Values.Select(x => Math.Round(x, 9).ToString("R", CultureInfo.InvariantCulture)));
    var rows = result.Rows
        .Select(x => (x.Bucket, Row: Row(x)))
        .OrderBy(x => x.Bucket)
        .ThenBy(x => x.Row, StringComparer.Ordinal)
        .Select(x => x.Bucket?.ToString("O", CultureInfo.InvariantCulture) + " " + x.Row);
    return string.Join("\n", rows.Prepend("total " + Row(result.Totals)));
  }

  [Test]
  public void ParallelScansMatchSequentialScans()
  {
    var events = Traffic();
    // A few events of the last minutes, for the live visitor count.
    for (var i = 0; i < 5; ++i)
      events.Add(new AnalyticsEvent("ping") { VisitorId = "live" + (i % 3).ToString(CultureInfo.InvariantCulture), Timestamp = DateTimeOffset.UtcNow.AddMinutes(-i) });

    var sequential = Run(1, events);
    Assert.Multiple(() =>
    {
      // The data is not trivial: sessions, funnel progress and live visitors are all present.
      Assert.That(sequential.Queries[0], Does.Contain("total "));
      Assert.That(sequential.Funnels[0], Does.Not.StartWith("0 "));
      Assert.That(sequential.Funnels[1], Does.Not.EndWith(" 0"));
      Assert.That(sequential.CurrentVisitors, Is.EqualTo(3));
    });
    foreach (var degree in new[] { 2, 3, 8 })
    {
      var parallel = Run(degree);
      Assert.Multiple(() =>
      {
        for (var i = 0; i < sequential.Queries.Length; ++i)
          Assert.That(parallel.Queries[i], Is.EqualTo(sequential.Queries[i]), $"query {i}, {degree} threads");
        Assert.That(parallel.Funnels, Is.EqualTo(sequential.Funnels), $"funnels, {degree} threads");
        Assert.That(parallel.CurrentVisitors, Is.EqualTo(sequential.CurrentVisitors), $"live visitors, {degree} threads");
      });
    }
  }

  [Test]
  public async Task ParallelEraseDeletesEveryEventOfTheVisitor()
  {
    // Monthly raw partitions: events in three months are erased on separate threads.
    var month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-3);
    using var engine = TestEngine.Create(directory, options => options.Queries.MaxDegreeOfParallelism = 4);
    for (var i = 0; i < 3; ++i)
    {
      engine.Track(new AnalyticsEvent("e") { VisitorId = "alice", Timestamp = Utc(month.AddMonths(i).AddDays(2)) });
      engine.Track(new AnalyticsEvent("e") { VisitorId = "bob", Timestamp = Utc(month.AddMonths(i).AddDays(2)) });
    }
    Assert.That(await engine.EraseVisitorAsync("alice"), Is.EqualTo(3));
    var remaining = engine.Query(new AnalyticsQuery
    {
      Event = "e",
      Range = DateRange.Between(Utc(month), Utc(month.AddMonths(3))),
      Source = QuerySource.RawEvents,
      Metrics = { Metric.Events, Metric.Visitors }
    });
    Assert.Multiple(() =>
    {
      Assert.That(remaining.Totals[Metric.Events], Is.EqualTo(3));
      Assert.That(remaining.Totals[Metric.Visitors], Is.EqualTo(1));
    });
  }

  [Test]
  public void CancelledParallelQueriesThrowOperationCanceled()
  {
    using var engine = TestEngine.Create(directory, options => options.Queries.MaxDegreeOfParallelism = 4);
    foreach (var e in Traffic())
      engine.Track(e);
    engine.Flush();
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Assert.Throws<OperationCanceledException>(() => engine.Query(Queries()[0], cancelled.Token));
  }

  [Test]
  public void ParallelWorkRunsEveryItemOnce()
  {
    var counts = new int[1000];
    ParallelWork.For(counts.Length, 8, i => Interlocked.Increment(ref counts[i]));
    Assert.That(counts, Is.All.EqualTo(1));
  }

  [Test]
  public void ParallelWorkRethrowsTheFailureAndSkipsTheRest()
  {
    var started = 0;
    var exception = Assert.Throws<InvalidOperationException>(() => ParallelWork.For(10_000, 4, i =>
    {
      Interlocked.Increment(ref started);
      if (i == 10)
        throw new InvalidOperationException("item 10");
      Thread.SpinWait(1000);
    }));
    Assert.Multiple(() =>
    {
      Assert.That(exception.Message, Is.EqualTo("item 10"));
      Assert.That(started, Is.LessThan(10_000));
    });
  }

  [Test]
  public void MaxDegreeOfParallelismMustBePositive()
  {
    var options = TestEngine.Options(directory, x => x.Queries.MaxDegreeOfParallelism = 0);
    Assert.Throws<ArgumentException>(() => new AnalyticsEngine(options).Dispose());
  }
}
