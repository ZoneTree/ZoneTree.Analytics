using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Query;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class CalculationFixRegressionTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-4);

  [TestCase("day", 330, false)]
  [TestCase("day", 345, true)]
  [TestCase("week", -210, false)]
  [TestCase("month", 330, true)]
  [TestCase("year", 345, false)]
  public void CalendarBucketBoundariesAgreeAcrossSources(string intervalName, int offsetMinutes, bool storeRaw)
  {
    var interval = TimeInterval.Parse(intervalName);
    var zone = TimeZoneInfo.CreateCustomTimeZone("audit", TimeSpan.FromMinutes(offsetMinutes), "audit", "audit");
    var calendar = new ReportingCalendar(zone, DayOfWeek.Monday);
    var localBoundary = new DateTime(2025, 1, interval == TimeInterval.Week ? 6 : 1);
    var boundary = calendar.UtcStartOfLocalDay(localBoundary.Ticks);
    var from = boundary - TimeSpan.TicksPerDay + TimeSpan.FromMinutes(5).Ticks;
    var to = boundary + TimeSpan.TicksPerDay - TimeSpan.FromMinutes(5).Ticks;
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Storage.StoreRawEvents = storeRaw;
      o.Rollups.Granularities |= Granularity.Minute;
    });
    foreach (var minutes in new[] { -1, 1 })
      engine.Track(new AnalyticsEvent("sample")
      {
        Timestamp = new DateTimeOffset(boundary + TimeSpan.FromMinutes(minutes).Ticks, TimeSpan.Zero)
      }.WithValue("x", minutes < 0 ? 10 : 20));
    engine.Flush();
    foreach (var source in storeRaw
        ? new[] { QuerySource.RawEvents, QuerySource.Auto, QuerySource.Rollups }
        : new[] { QuerySource.Auto, QuerySource.Rollups })
    {
      var result = engine.Query(new AnalyticsQuery
      {
        Event = "sample", Range = DateRange.Between(new(from, TimeSpan.Zero), new(to, TimeSpan.Zero)),
        TimeZone = zone, Interval = interval, Source = source,
        Metrics = { Metric.Events, Metric.Sum("x") }
      });
      var rows = result.Rows.Where(r => r[Metric.Events] > 0).ToArray();
      Assert.Multiple(() =>
      {
        Assert.That(rows.Select(r => r[Metric.Events]), Is.EqualTo(new double[] { 1, 1 }));
        Assert.That(rows.Select(r => r[Metric.Sum("x")]), Is.EqualTo(new double[] { 10, 20 }));
        Assert.That(result.Totals[Metric.Events], Is.EqualTo(2));
        Assert.That(result.Plan.IsComplete, Is.True);
      });
    }
  }

  [Test]
  public void CalendarBoundaryWithoutFineDataReportsMissingCoverage()
  {
    using var engine = TestEngine.Create(directory, o => o.Storage.StoreRawEvents = false);
    foreach (var minute in new[] { 15, 45 })
      engine.Track(new AnalyticsEvent("sample") { Timestamp = Utc(Day0.AddHours(18).AddMinutes(minute)) });
    engine.Flush();
    var zone = TimeZoneInfo.CreateCustomTimeZone("audit", TimeSpan.FromMinutes(330), "audit", "audit");
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "sample", Range = Range(Day0, Day0.AddDays(2)), TimeZone = zone,
      Interval = TimeInterval.Day, Metrics = { Metric.Events }
    });
    Assert.That(result.Plan.IsComplete, Is.False);
    Assert.That(result.Totals[Metric.Events], Is.Zero, "The crossing hour cannot be assigned exactly to either day.");
  }

  [TestCase(1)]
  [TestCase(4)]
  public void WindowFunnelsMatchAnIndependentAttemptEnumerator(int parallelism)
  {
    using var engine = TestEngine.Create(directory, o => o.Queries.MaxDegreeOfParallelism = parallelism);
    var random = new Random(8191);
    var visitors = Enumerable.Range(0, 40).Select(v =>
        Enumerable.Range(0, 30).Select(i => (Name: "e" + random.Next(3), Minute: i + random.Next(2))).ToArray()).ToArray();
    for (var v = 0; v < visitors.Length; ++v)
    {
      foreach (var hit in visitors[v])
        engine.Track(new AnalyticsEvent(hit.Name)
        {
          VisitorId = "v" + v, Timestamp = Utc(Day0.AddHours(10).AddMinutes(hit.Minute))
        });
    }
    engine.Flush();
    // Keep every attempt, independently of the optimized latest-start-per-prefix algorithm.
    int Best((string Name, int Minute)[] hits)
    {
      var best = 0;
      for (var start = 0; start < hits.Length; ++start)
      {
        if (hits[start].Name != "e0")
          continue;
        var reached = 1;
        for (var i = start + 1; i < hits.Length && reached < 3; ++i)
        {
          if (hits[i].Minute - hits[start].Minute > 5)
            break;
          if (hits[i].Name == "e" + reached)
            ++reached;
        }
        best = Math.Max(best, reached);
      }
      return best;
    }
    var best = visitors.Select(Best).ToArray();
    var result = engine.Query(new FunnelQuery
    {
      Range = Day(Day0), Window = TimeSpan.FromMinutes(5),
      Steps = { FunnelStep.Goal("e0"), FunnelStep.Goal("e1"), FunnelStep.Goal("e2") }
    });
    Assert.That(result.Steps.Select(s => s.Visitors),
        Is.EqualTo(Enumerable.Range(1, 3).Select(step => (long)best.Count(b => b >= step))));
  }

  [TestCase("0:a,5:b,6:a,9:c", 1)]
  [TestCase("0:a,5:b,6:a,11:c", 0)]
  [TestCase("0:a,5:a,6:b,15:c", 1)]
  [TestCase("0:a,5:a,6:b,16:c", 0)]
  public void OverlappingFunnelsPreserveOnlyValidOrderedAttempts(string events, int completed)
  {
    using var engine = TestEngine.Create(directory);
    foreach (var item in events.Split(','))
    {
      var parts = item.Split(':');
      engine.Track(new AnalyticsEvent(parts[1])
      {
        VisitorId = "alice", Timestamp = Utc(Day0.AddMinutes(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)))
      });
    }
    engine.Flush();
    var result = engine.Query(new FunnelQuery
    {
      Range = Day(Day0), Window = TimeSpan.FromMinutes(10),
      Steps = { FunnelStep.Goal("a"), FunnelStep.Goal("b"), FunnelStep.Goal("c") }
    });
    Assert.That(result.Steps[^1].Visitors, Is.EqualTo(completed));
  }

  [TestCase(2)]
  [TestCase(64)]
  public void OneEventCompletesAtMostOneFunnelStep(int steps)
  {
    using var engine = TestEngine.Create(directory);
    for (var i = 0; i < steps - 1; ++i)
      engine.Track(new AnalyticsEvent("a") { VisitorId = "alice", Timestamp = Utc(Day0.AddMinutes(i)) });
    engine.Flush();
    var query = new FunnelQuery { Range = Day(Day0), Window = TimeSpan.FromHours(2) };
    query.Steps.AddRange(Enumerable.Repeat(FunnelStep.Goal("a"), steps));
    var result = engine.Query(query);
    Assert.That(result.Steps[^2].Visitors, Is.EqualTo(1));
    Assert.That(result.Steps[^1].Visitors, Is.Zero);
  }

  [TestCase(1, true)]
  [TestCase(4, true)]
  [TestCase(1, false)]
  [TestCase(4, false)]
  public void LateEventsAndStartTimestampTiesPreserveTheActualEntrance(int parallelism, bool firstIsPage)
  {
    using var engine = TestEngine.Create(directory, o => o.Queries.MaxDegreeOfParallelism = parallelism);
    void Track(string name, string page, int minute) => engine.Track(new AnalyticsEvent(name)
    {
      VisitorId = "alice", Url = page, Timestamp = Utc(Day0.AddHours(10).AddMinutes(minute))
    });
    Track(firstIsPage ? "pageview" : "activity", "/entry", 10);
    Track("pageview", "/late", 5);
    Track("pageview", "/tie", 10);
    Track("pageview", "/exit", 15);
    engine.Flush();
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups, QuerySource.Auto })
    {
      var result = engine.Query(new AnalyticsQuery
      {
        Range = Range(Day0.AddHours(10), Day0.AddHours(10).AddMinutes(30)), Source = source,
        GroupBy = { StandardDimensions.Page }, Metrics = { Metric.Entrances }
      });
      Assert.That(result.Totals[Metric.Entrances], Is.EqualTo(firstIsPage ? 1 : 0));
      if (firstIsPage)
        Assert.That(result.Rows.Single(r => r[StandardDimensions.Page] == "/entry")[Metric.Entrances], Is.EqualTo(1));
    }
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  [TestCase(QuerySource.Auto)]
  public void BaseRowRestrictionIsIndependentOfConditionalMetricsAndPreservesTotals(QuerySource source)
  {
    using var engine = TestEngine.Create(directory);
    engine.Track(new AnalyticsEvent("signup") { Timestamp = Utc(Day0) }.WithDimension("country", "DE"));
    engine.Track(new AnalyticsEvent("pageview") { Timestamp = Utc(Day0) }.WithDimension("country", "US"));
    engine.Flush();
    var signups = Metric.EventsOf("signup").Where(Filter.Equal("country", "DE"));
    var result = engine.Query(new AnalyticsQuery
    {
      Range = Day(Day0), Source = source, OnlyRowsWithBaseEvent = true,
      GroupBy = { "country" }, Metrics = { signups }
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Rows.Single()["country"], Is.EqualTo("US"));
      Assert.That(result.Rows.Single()[signups], Is.Zero);
      Assert.That(result.Totals[signups], Is.EqualTo(1));
    });
  }

  [Test]
  public void PopulationMomentsMatchScaledArithmeticAcrossExtremeMagnitudes()
  {
    double[][] samples =
    [
      [1e308, 1e308, 1e308], [1e308, 1e308, -1e308],
      [-double.MaxValue, double.MaxValue, -double.MaxValue, double.MaxValue],
      [-1e154, 1e154], [-1e-160, 1e-160]
    ];
    var random = new Random(65537);
    samples = [.. samples, Enumerable.Range(0, 300).Select(_ =>
        (random.Next(2) == 0 ? -1 : 1) * Math.Pow(10, random.NextDouble() * 300)).ToArray()];
    var serializer = new RollupValueSerializer();
    foreach (var values in samples)
    {
      var scale = values.Max(Math.Abs);
      var normalized = values.Select(v => v / scale).ToArray();
      var mean = normalized.Average();
      var spread = Math.Sqrt(normalized.Average(v => (v - mean) * (v - mean))) * scale;
      var parts = new[] { default(ValueStats), default(ValueStats), default(ValueStats) };
      for (var i = 0; i < values.Length; ++i)
        parts[i % parts.Length] = ValueStats.Combine(parts[i % parts.Length], ValueStats.Of(values[i]));
      var aggregate = parts.Aggregate(ValueStats.Combine);
      var stored = new RollupValue(values.Length, null, ["x"], [aggregate]);
      var restored = serializer.Deserialize(serializer.Serialize(stored)).Values[0];
      Assert.Multiple(() =>
      {
        Assert.That(double.IsFinite(restored.Average), Is.True);
        Assert.That(double.IsFinite(restored.StandardDeviation), Is.True);
        Assert.That(restored.Average, Is.EqualTo(mean * scale).Within(scale * 1e-12));
        Assert.That(restored.StandardDeviation, Is.EqualTo(spread).Within(scale * 1e-12));
        if (spread == 0)
          Assert.That(restored.Variance, Is.Zero);
      });
    }
  }

  [Test]
  public void LargeMomentsSurviveFlushCompactionReopenAndFurtherMerges()
  {
    using (var engine = TestEngine.Create(directory))
    {
      for (var i = 0; i < 3; ++i)
      {
        engine.Track(new AnalyticsEvent("sample") { Timestamp = Utc(Day0) }.WithValue("x", 1e308));
        engine.Flush();
      }
      engine.Compact();
    }
    using var reopened = TestEngine.Create(directory);
    reopened.Track(new AnalyticsEvent("sample") { Timestamp = Utc(Day0) }.WithValue("x", 1e308));
    reopened.Flush();
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Rollups, QuerySource.Auto })
    {
      var result = reopened.Query(new AnalyticsQuery
      {
        Event = "sample", Range = Day(Day0), Source = source,
        Metrics = { Metric.Count("x"), Metric.Average("x"), Metric.Variance("x"), Metric.StandardDeviation("x") }
      });
      Assert.Multiple(() =>
      {
        Assert.That(result.Totals[Metric.Count("x")], Is.EqualTo(4));
        Assert.That(result.Totals[Metric.Average("x")], Is.EqualTo(1e308));
        Assert.That(result.Totals[Metric.Variance("x")], Is.Zero);
        Assert.That(result.Totals[Metric.StandardDeviation("x")], Is.Zero);
      });
    }
  }

  [Test]
  public void StandardDeviationRemainsFiniteWhenVarianceIsUnrepresentable()
  {
    using var engine = TestEngine.Create(directory);
    foreach (var x in new[] { -double.MaxValue, double.MaxValue })
      engine.Track(new AnalyticsEvent("sample") { Timestamp = Utc(Day0) }.WithValue("x", x));
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "sample", Range = Day(Day0), Metrics = { Metric.Average("x"), Metric.StandardDeviation("x") }
    });
    Assert.That(result.Totals[Metric.Average("x")], Is.Zero);
    Assert.That(result.Totals[Metric.StandardDeviation("x")], Is.EqualTo(double.MaxValue).Within(double.MaxValue * 1e-14));
  }

  [Test]
  public void ExtremeQuantilesAndHistogramCountsSurviveMergingAndSerialization()
  {
    double[] values = [-double.MaxValue, -1e308, 1e308, double.MaxValue];
    var sketch = new QuantileSketch();
    foreach (var value in values)
    {
      var part = new QuantileSketch();
      part.Add(value);
      sketch.MergeWith(part);
    }
    var writer = new ByteWriter();
    sketch.Write(writer);
    var reader = new ByteReader(writer.ToArray());
    var restored = QuantileSketch.Read(ref reader);
    Assert.Multiple(() =>
    {
      Assert.That(restored.CountBetween(double.NegativeInfinity, double.PositiveInfinity), Is.EqualTo(4));
      Assert.That(restored.CountBetween(double.NegativeInfinity, 0), Is.EqualTo(2));
      Assert.That(restored.CountBetween(0, double.PositiveInfinity), Is.EqualTo(2));
      foreach (var q in new[] { 0.0, 0.5, 0.9, 1.0 })
      {
        var exact = values[(int)Math.Floor(q * (values.Length - 1))];
        Assert.That(restored.Quantile(q), Is.EqualTo(exact).Within(Math.Abs(exact) * 0.0101));
      }
    });
  }
}
