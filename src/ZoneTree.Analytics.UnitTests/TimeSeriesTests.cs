using System.Globalization;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Time series and metrics capabilities: percentiles, first and last values, distinct counts,
/// variance, bucket widths, fill modes, formulas, series functions, Having and numeric filters.
/// </summary>
public sealed class TimeSeriesTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-10);

  AnalyticsEngine engine;

  [TearDown]
  public void TearDown()
  {
    engine?.Dispose();
  }

  static void ConfigureAggregates(AnalyticsOptions options)
  {
    options.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
    options.Rollups.ValueAggregates["load_time"] = ValueAggregates.All;
    options.Rollups.ValueAggregates["temperature"] = ValueAggregates.FirstAndLast;
    options.Rollups.DistinctDimensions.Add("tenant");
    options.Rollups.Definitions.Add(new RollupDefinition("status"));
  }

  /// <summary>
  /// Requests over two days with load times, statuses and tenants. Returns the load times.
  /// </summary>
  List<(DateTime Time, double LoadTime, string Status, string Tenant)> TrackRequests(int count = 600)
  {
    var random = new Random(7);
    var requests = new List<(DateTime, double, string, string)>();
    for (var i = 0; i < count; ++i)
    {
      var time = Day0.AddMinutes(i * 4.8);
      // Log-normal latencies between about 20 ms and several seconds.
      var loadTime = Math.Round(Math.Exp(5 + random.NextDouble() * 3 * (random.NextDouble() - 0.3)), 3);
      var status = random.Next(10) switch { 0 => "500", 1 => "404", _ => "200" };
      var tenant = "t" + (i % 7).ToString(CultureInfo.InvariantCulture);
      requests.Add((time, loadTime, status, tenant));
      engine.Track(new AnalyticsEvent("request")
      {
        VisitorId = "u" + (i % 31).ToString(CultureInfo.InvariantCulture),
        Timestamp = Utc(time),
        Dimensions = { ["status"] = status, ["tenant"] = tenant },
        Values = { ["load_time"] = loadTime }
      });
    }
    engine.Flush();
    return requests;
  }

  AnalyticsResult Query(Action<AnalyticsQuery> configure)
  {
    var query = new AnalyticsQuery { Event = "request", Range = Range(Day0, Day0.AddDays(2)) };
    configure(query);
    return engine.Query(query);
  }

  /// <summary>
  /// Rows with values to 12 significant digits: sums and variances depend on the merge order in
  /// their last bits.
  /// </summary>
  static string Describe(AnalyticsResult result) =>
      string.Join("\n", result.Rows.Append(result.Totals).Select(row =>
          row.Bucket?.ToString("O", CultureInfo.InvariantCulture) + " [" + string.Join(",", row.Dimensions) + "] " +
          string.Join(" ", row.Values.Select(x => x.ToString("G12", CultureInfo.InvariantCulture)))));

  #region Sketches and storage

  [Test]
  public void QuantileSketchIsWithinOnePercent()
  {
    var random = new Random(3);
    var values = Enumerable.Range(0, 20_000)
        .Select(_ => Math.Exp(random.NextDouble() * 12 - 4) * (random.Next(5) == 0 ? -1 : 1))
        .Append(0)
        .ToArray();
    var sketch = new QuantileSketch();
    foreach (var value in values)
      sketch.Add(value);
    Array.Sort(values);
    Assert.Multiple(() =>
    {
      Assert.That(sketch.Count, Is.EqualTo(values.Length));
      foreach (var q in new[] { 0.01, 0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 0.99, 0.999 })
      {
        var exact = values[(int)Math.Floor(q * (values.Length - 1))];
        Assert.That(sketch.Quantile(q), Is.EqualTo(exact).Within(Math.Abs(exact) * 0.0101 + 1e-9), $"q={q}");
      }
    });
  }

  [Test]
  public void QuantileSketchesMergeExactlyAndRoundTrip()
  {
    var random = new Random(5);
    var whole = new QuantileSketch();
    var parts = Enumerable.Range(0, 4).Select(_ => new QuantileSketch()).ToArray();
    for (var i = 0; i < 5000; ++i)
    {
      var value = random.NextDouble() * 1000 - 100;
      whole.Add(value);
      parts[i % 4].Add(value);
    }
    var merged = parts[3].Clone();
    merged.MergeWith(parts[1]);
    merged.MergeWith(parts[0]);
    merged.MergeWith(parts[2]);
    var writer = new ByteWriter(64);
    merged.Write(writer);
    var reader = new ByteReader(writer.ToArray());
    var restored = QuantileSketch.Read(ref reader);
    Assert.Multiple(() =>
    {
      foreach (var q in new[] { 0, 0.3, 0.5, 0.99, 1 })
      {
        Assert.That(merged.Quantile(q), Is.EqualTo(whole.Quantile(q)), $"merged q={q}");
        Assert.That(restored.Quantile(q), Is.EqualTo(whole.Quantile(q)), $"restored q={q}");
      }
      Assert.That(restored.Count, Is.EqualTo(5000));
    });
  }

  [Test]
  public void QuantileSketchBoundsItsSizeOverHugeRanges()
  {
    var sketch = new QuantileSketch();
    for (var exponent = -9; exponent <= 300; exponent += 1)
      sketch.Add(Math.Pow(10, exponent));
    sketch.Add(1e300);
    var writer = new ByteWriter(64);
    sketch.Write(writer);
    Assert.Multiple(() =>
    {
      Assert.That(sketch.Count, Is.EqualTo(311));
      // The largest values keep their accuracy; the smallest magnitudes share a bucket.
      Assert.That(sketch.Quantile(1), Is.EqualTo(1e300).Within(1e300 * 0.0101));
      Assert.That(writer.ToArray(), Has.Length.LessThan(2048 * 3));
    });
  }

  [Test]
  public void VarianceMergesAccuratelyWithLargeMeans()
  {
    var values = Enumerable.Range(0, 1000).Select(i => 1e9 + i % 10).ToArray();
    var mean = values.Average();
    var exact = values.Sum(x => (x - mean) * (x - mean)) / values.Length;
    var parts = new[] { new RollupAccumulator(), new RollupAccumulator(), new RollupAccumulator() };
    for (var i = 0; i < values.Length; ++i)
      parts[i % 3].AddValue("x", values[i]);
    var total = new RollupAccumulator();
    foreach (var part in parts)
      total.Merge(RollupValue.Merge(null, part.ToValue()));
    Assert.That(total.TryGetValueStats("x", out var x) ? x.Variance : -1, Is.EqualTo(exact).Within(1e-6));
  }

  [Test]
  public void AccumulatorsNeverModifyTheRowsTheyShare()
  {
    var source = new RollupAccumulator { Count = 1 };
    source.AddValue("a", 1);
    source.AddValueDelta("b", 2);
    source.AddValueDetail("a", 1, 10, ValueAggregates.All, QuantileSketch.BucketOf(1));
    var stored = source.ToValue();
    var group = new RollupAccumulator();
    group.Merge(stored);
    group.AddValue("a", 5);
    group.AddValueDelta("c", 3);
    group.Merge(stored);
    group.AddValueDetail("a", 9, 20, ValueAggregates.All, QuantileSketch.BucketOf(9));
    Assert.Multiple(() =>
    {
      Assert.That(stored.Values, Is.EqualTo(new[] { ValueStats.Of(1), ValueStats.Delta(2) }));
      Assert.That(stored.DetailAt(0).Quantiles.Count, Is.EqualTo(1));
      Assert.That(stored.DetailAt(0).Last, Is.EqualTo(1));
      Assert.That(group.TryGetValueStats("a", out var a) ? (a.Count, a.Sum) : default, Is.EqualTo((3L, 7.0)));
      Assert.That(group.TryGetValueStats("b", out var b) ? b.Sum : 0, Is.EqualTo(4));
      Assert.That(group.TryGetValueStats("c", out var c) ? c.Sum : 0, Is.EqualTo(3));
      Assert.That(group.DetailOf("a").Last, Is.EqualTo(9));
      Assert.That(group.DetailOf("a").Quantiles.Count, Is.EqualTo(3));
    });
  }

  [Test]
  public void RollupValuesRoundTripDetailsAndDistinctCounts()
  {
    var accumulator = new RollupAccumulator { Count = 3 };
    accumulator.AddValue("load_time", 10);
    accumulator.AddValue("load_time", 30);
    accumulator.AddValueDetail("load_time", 10, 100, ValueAggregates.All, QuantileSketch.BucketOf(10));
    accumulator.AddValueDetail("load_time", 30, 50, ValueAggregates.All, QuantileSketch.BucketOf(30));
    accumulator.AddValueDelta("$exits", 1);
    accumulator.AddDistinct("tenant", Text.StableHash("a"), 14);
    accumulator.AddDistinct("tenant", Text.StableHash("b"), 14);
    var value = accumulator.ToValue();
    var serializer = new RollupValueSerializer();
    var restored = serializer.Deserialize(serializer.Serialize(value));
    var merged = RollupValue.Merge(restored, restored);
    Assert.Multiple(() =>
    {
      Assert.That(restored.Values, Is.EqualTo(value.Values));
      Assert.That(restored.DetailAt(0), Is.Null, "$exits has no details");
      var detail = restored.DetailAt(1);
      Assert.That((detail.First, detail.FirstTicks, detail.Last, detail.LastTicks), Is.EqualTo((30.0, 50L, 10.0, 100L)));
      Assert.That(detail.Quantiles.Count, Is.EqualTo(2));
      Assert.That(restored.DistinctNames, Is.EqualTo(new[] { "tenant" }));
      Assert.That(restored.Distinct[0].Estimate(), Is.EqualTo(2));
      Assert.That(merged.DetailAt(1).Quantiles.Count, Is.EqualTo(4));
      Assert.That(restored.DetailAt(1).Quantiles.Count, Is.EqualTo(2), "merging never modifies its inputs");
      Assert.That(merged.Distinct[0].Estimate(), Is.EqualTo(2));
    });
  }

  #endregion

  #region Aggregates

  [Test]
  public void OptInAggregatesFromRollupsEqualRawEventsAndExactValues()
  {
    engine = TestEngine.Create(directory, ConfigureAggregates);
    var requests = TrackRequests();
    var metrics = new[]
    {
      Metric.Median("load_time"), Metric.Percentile("load_time", 95), Metric.Percentile("load_time", 99),
      Metric.Percentile("load_time", 0), Metric.Percentile("load_time", 100), Metric.First("load_time"),
      Metric.Last("load_time"), Metric.Distinct("tenant"), Metric.StandardDeviation("load_time"),
      Metric.Variance("load_time"), Metric.Average("load_time"), Metric.Count("load_time"),
    };
    AnalyticsResult Run(QuerySource source, TimeInterval interval, params string[] groupBy) => Query(q =>
    {
      q.Source = source;
      q.Interval = interval;
      q.GroupBy.AddRange(groupBy);
      q.Metrics.AddRange(metrics);
    });
    foreach (var (interval, groupBy) in new[] { (TimeInterval.None, Array.Empty<string>()), (TimeInterval.Hour, ["status"]), (TimeInterval.Day, ["status"]) })
    {
      var rollups = Run(QuerySource.Rollups, interval, groupBy);
      var raw = Run(QuerySource.RawEvents, interval, groupBy);
      Assert.That(rollups.Plan.UsedRawEvents, Is.False);
      Assert.That(raw.Plan.UsedRollups, Is.False);
      Assert.That(Describe(rollups), Is.EqualTo(Describe(raw)), $"{interval} by [{string.Join(",", groupBy)}]");
    }

    var totals = Run(QuerySource.Auto, TimeInterval.None).Totals;
    var sorted = requests.Select(x => x.LoadTime).Order().ToArray();
    double Exact(double q) => sorted[(int)Math.Floor(q * (sorted.Length - 1))];
    var mean = sorted.Average();
    Assert.Multiple(() =>
    {
      Assert.That(totals[metrics[0]], Is.EqualTo(Exact(0.5)).Within(Exact(0.5) * 0.0101));
      Assert.That(totals[metrics[1]], Is.EqualTo(Exact(0.95)).Within(Exact(0.95) * 0.0101));
      Assert.That(totals[metrics[2]], Is.EqualTo(Exact(0.99)).Within(Exact(0.99) * 0.0101));
      Assert.That(totals[metrics[3]], Is.EqualTo(sorted[0]));
      Assert.That(totals[metrics[4]], Is.EqualTo(sorted[^1]));
      Assert.That(totals[metrics[5]], Is.EqualTo(requests[0].LoadTime));
      Assert.That(totals[metrics[6]], Is.EqualTo(requests[^1].LoadTime));
      Assert.That(totals[metrics[7]], Is.EqualTo(7));
      Assert.That(totals[metrics[9]], Is.EqualTo(sorted.Average(x => (x - mean) * (x - mean))).Within(1e-6).Percent);
      Assert.That(totals[metrics[11]], Is.EqualTo(requests.Count));
    });
  }

  [Test]
  public void AggregatesConfiguredLaterAnswerEarlierRangesFromRawEvents()
  {
    engine = TestEngine.Create(directory, o => o.Rollups.Granularities = Granularity.Hour | Granularity.Day);
    TrackRequests(100);
    engine.Dispose();
    engine = TestEngine.Create(directory, ConfigureAggregates);
    var p95 = Metric.Percentile("load_time", 95);
    var auto = Query(q => q.Metrics.Add(p95));
    var raw = Query(q =>
    {
      q.Source = QuerySource.RawEvents;
      q.Metrics.Add(p95);
    });
    var events = Query(q => q.Metrics.Add(Metric.Events));
    Assert.Multiple(() =>
    {
      Assert.That(auto.Plan.UsedRollups, Is.False, "the range predates the percentile rollups");
      Assert.That(auto.Totals[p95], Is.EqualTo(raw.Totals[p95]));
      Assert.That(auto.Totals[p95], Is.GreaterThan(0));
      Assert.That(events.Plan.UsedRawEvents, Is.False, "standard aggregates still come from rollups");
    });
  }

  [Test]
  public void MissingOptInAggregatesWithoutRawEventsFail()
  {
    engine = TestEngine.Create(directory, o => o.Storage.StoreRawEvents = false);
    TrackRequests(10);
    var exception = Assert.Throws<AnalyticsQueryException>(() => Query(q => q.Metrics.Add(Metric.Percentile("load_time", 90))));
    Assert.That(exception.Message, Does.Contain("percentiles of 'load_time'").And.Contain("Rollups.ValueAggregates"));
    exception = Assert.Throws<AnalyticsQueryException>(() => Query(q => q.Metrics.Add(Metric.Distinct("tenant"))));
    Assert.That(exception.Message, Does.Contain("distinct values of 'tenant'"));
  }

  #endregion

  #region Intervals and fill

  [Test]
  public void TimeIntervalsParseAndValidate()
  {
    Assert.Multiple(() =>
    {
      Assert.That(TimeInterval.Minutes(5).ToString(), Is.EqualTo("5m"));
      Assert.That(TimeInterval.Parse("15m"), Is.EqualTo(TimeInterval.Minutes(15)));
      Assert.That(TimeInterval.Parse("60m"), Is.EqualTo(TimeInterval.Hour));
      Assert.That(TimeInterval.Parse("1h"), Is.EqualTo(TimeInterval.Hour));
      Assert.That(TimeInterval.Parse("6h").ToString(), Is.EqualTo("6h"));
      Assert.That(TimeInterval.Minutes(120), Is.EqualTo(TimeInterval.Hours(2)));
      Assert.That(TimeInterval.Parse("Week"), Is.EqualTo(TimeInterval.Week));
      Assert.That(TimeInterval.Parse("none"), Is.EqualTo(TimeInterval.None));
      Assert.That(default(TimeInterval), Is.EqualTo(TimeInterval.None));
      Assert.That(TimeInterval.TryParse("7m", out _), Is.False);
      Assert.That(TimeInterval.TryParse("24h", out _), Is.False);
      Assert.That(TimeInterval.TryParse("5x", out _), Is.False);
      Assert.Throws<ArgumentOutOfRangeException>(() => TimeInterval.Minutes(0));
      Assert.Throws<ArgumentOutOfRangeException>(() => TimeInterval.Hours(5));
      Assert.Throws<FormatException>(() => TimeInterval.Parse("fortnight"));
    });
  }

  [Test]
  public void FixedWidthBucketsUseTheFinestRollupThatFits()
  {
    engine = TestEngine.Create(directory, ConfigureAggregates);
    for (var minute = 0; minute < 120; ++minute)
      engine.Track(new AnalyticsEvent("tick") { VisitorId = "v", Timestamp = Utc(Day0.AddMinutes(minute)) });
    engine.Flush();
    AnalyticsResult Run(TimeInterval interval, QuerySource source = QuerySource.Auto) => engine.Query(new AnalyticsQuery
    {
      Event = "tick",
      Range = Range(Day0, Day0.AddHours(12)),
      Interval = interval,
      Source = source,
      Metrics = { Metric.Events }
    });
    var fiveMinutes = Run(TimeInterval.Minutes(5));
    var sixHours = Run(TimeInterval.Hours(6));
    Assert.Multiple(() =>
    {
      Assert.That(fiveMinutes.Rows, Has.Count.EqualTo(144));
      Assert.That(fiveMinutes.Rows.Take(24).Select(x => x[Metric.Events]), Is.All.EqualTo(5));
      Assert.That(fiveMinutes.Rows[1].Bucket, Is.EqualTo(Utc(Day0.AddMinutes(5))));
      Assert.That(fiveMinutes.Plan.Steps.Select(x => x.Source), Is.All.EqualTo(PlanSource.MinuteRollups));
      Assert.That(Describe(fiveMinutes), Is.EqualTo(Describe(Run(TimeInterval.Minutes(5), QuerySource.RawEvents))));
      Assert.That(fiveMinutes.Interval.ToString(), Is.EqualTo("5m"));
      Assert.That(sixHours.Rows.Select(x => x[Metric.Events]), Is.EqualTo(new double[] { 120, 0 }));
      Assert.That(sixHours.Plan.Steps.Select(x => x.Source), Is.All.EqualTo(PlanSource.HourRollups));
    });
  }

  [Test]
  public void FillModesFillEveryGroup()
  {
    engine = TestEngine.Create(directory, ConfigureAggregates);
    void Reading(string room, int hour, double value) => engine.Track(new AnalyticsEvent("reading")
    {
      VisitorId = room,
      Timestamp = Utc(Day0.AddHours(hour)),
      Dimensions = { ["room"] = room },
      Values = { ["temperature"] = value }
    });
    Reading("a", 0, 10);
    Reading("a", 3, 40);
    Reading("b", 2, 5);
    engine.Flush();
    double[] Series(FillMode fill, string room) => engine.Query(new AnalyticsQuery
    {
      Event = "reading",
      Range = Range(Day0, Day0.AddHours(6)),
      Interval = TimeInterval.Hour,
      GroupBy = { "room" },
      Metrics = { Metric.Last("temperature") },
      Fill = fill
    }).Rows.Where(x => x["room"] == room).Select(x => x[Metric.Last("temperature")]).ToArray();
    Assert.Multiple(() =>
    {
      Assert.That(Series(FillMode.None, "a"), Is.EqualTo(new double[] { 10, 40 }));
      Assert.That(Series(FillMode.Zero, "a"), Is.EqualTo(new double[] { 10, 0, 0, 40, 0, 0 }));
      Assert.That(Series(FillMode.Previous, "a"), Is.EqualTo(new double[] { 10, 10, 10, 40, 40, 40 }));
      Assert.That(Series(FillMode.Linear, "a"), Is.EqualTo(new double[] { 10, 20, 30, 40, 40, 40 }));
      Assert.That(Series(FillMode.Previous, "b"), Is.EqualTo(new double[] { 5, 5, 5, 5, 5, 5 }));
      Assert.That(Series(FillMode.Zero, "b"), Is.EqualTo(new double[] { 0, 0, 5, 0, 0, 0 }));
    });
  }

  [Test]
  public void FillingIsBounded()
  {
    engine = TestEngine.Create(directory);
    for (var i = 0; i < 11; ++i)
      engine.Track(new AnalyticsEvent("e") { VisitorId = "v", Timestamp = Utc(Day0), Dimensions = { ["k"] = i.ToString(CultureInfo.InvariantCulture) } });
    engine.Flush();
    var query = new AnalyticsQuery
    {
      Event = "e",
      Range = Range(Day0, Day0.AddDays(69)),
      Interval = TimeInterval.Minute,
      GroupBy = { "k" },
      Source = QuerySource.RawEvents
    };
    Assert.Throws<AnalyticsQueryException>(() => engine.Query(query));
    query.Fill = FillMode.None;
    Assert.That(engine.Query(query).Rows, Has.Count.EqualTo(11));
  }

  #endregion

  #region Formulas and series functions

  [Test]
  public void FormulasAndSeriesFunctions()
  {
    engine = TestEngine.Create(directory, ConfigureAggregates);
    double[] revenue = [10, 30, 20, 40];
    double[] meter = [100, 150, 20, 70];
    for (var day = 0; day < 4; ++day)
    {
      engine.Track(new AnalyticsEvent("purchase") { VisitorId = "a", Timestamp = Utc(Day0.AddDays(day).AddHours(1)), Values = { ["revenue"] = revenue[day] } });
      engine.Track(new AnalyticsEvent("purchase") { VisitorId = "b", Timestamp = Utc(Day0.AddDays(day).AddHours(2)), Values = { ["revenue"] = revenue[day] } });
      engine.Track(new AnalyticsEvent("meter") { VisitorId = "m", Timestamp = Utc(Day0.AddDays(day).AddHours(3)), Values = { ["temperature"] = meter[day] } });
    }
    engine.Flush();
    var orders = Metric.EventsOf("purchase");
    var sum = Metric.Sum("revenue", "purchase");
    var averageOrder = sum / orders;
    var cumulative = Metric.Cumulative(sum);
    var moving = Metric.MovingAverage(sum, 2);
    var difference = Metric.Difference(sum);
    var increase = Metric.Increase(Metric.Last("temperature", "meter"));
    var perHour = Metric.Rate(orders, TimeSpan.FromHours(1));
    var doubled = 2 * sum + 1;
    var zeroDivision = sum / Metric.Constant(0);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "purchase",
      Range = Range(Day0, Day0.AddDays(4)),
      Interval = TimeInterval.Day,
      Metrics = { averageOrder, cumulative, moving, difference, increase, perHour, doubled, zeroDivision },
    });
    double[] Column(Metric metric) => result.Rows.Select(x => x[metric]).ToArray();
    Assert.Multiple(() =>
    {
      Assert.That(Column(averageOrder), Is.EqualTo(revenue));
      Assert.That(result.Totals[averageOrder], Is.EqualTo(25), "a formula of totals, not a sum of rows");
      Assert.That(Column(cumulative), Is.EqualTo(new double[] { 20, 80, 120, 200 }));
      Assert.That(result.Totals[cumulative], Is.EqualTo(200));
      Assert.That(Column(moving), Is.EqualTo(new double[] { 20, 40, 50, 60 }));
      Assert.That(Column(difference), Is.EqualTo(new double[] { 0, 40, -20, 40 }));
      Assert.That(result.Totals[difference], Is.EqualTo(60));
      Assert.That(Column(increase), Is.EqualTo(new double[] { 0, 50, 20, 50 }), "a decrease is a counter reset");
      Assert.That(result.Totals[increase], Is.EqualTo(120));
      Assert.That(Column(perHour), Is.All.EqualTo(2.0 / 24));
      Assert.That(result.Totals[perHour], Is.EqualTo(8.0 / 96));
      Assert.That(Column(doubled), Is.EqualTo(new double[] { 41, 121, 81, 161 }));
      Assert.That(Column(zeroDivision), Is.All.EqualTo(0));
    });
    // The same keys parse back to the same metrics.
    Assert.That(Metric.Parse(averageOrder.Key), Is.EqualTo(averageOrder));
    Assert.That(Metric.Parse(increase.Key), Is.EqualTo(increase));
  }

  [Test]
  public void SeriesFunctionsNeedAnInterval()
  {
    engine = TestEngine.Create(directory);
    var exception = Assert.Throws<AnalyticsQueryException>(() => engine.Query(new AnalyticsQuery
    {
      Metrics = { Metric.Cumulative(Metric.Events) }
    }));
    Assert.That(exception.Message, Does.Contain("Interval"));
    // A rate needs no interval: it is the average rate over the range.
    Assert.DoesNotThrow(() => engine.Query(new AnalyticsQuery { Metrics = { Metric.Rate(Metric.Events) } }));
  }

  #endregion

  #region Conditional metrics, histograms, lag and lead, top rows per bucket

  [Test]
  public void ConditionalMetricsEqualFilteredQueries()
  {
    engine = TestEngine.Create(directory, o =>
    {
      ConfigureAggregates(o);
      o.Rollups.Definitions.Add(new RollupDefinition("tenant"));
      o.Rollups.Definitions.Add(new RollupDefinition("status", "tenant"));
    });
    var requests = TrackRequests(400);
    var isError = Filter.GreaterThanOrEqual("status", 500);
    var errors = Metric.Events.Where(isError);
    var errorRate = errors / Metric.Events;
    var okLoad = Metric.Sum("load_time").Where(Filter.Equal("status", "200"));
    var errorP95 = Metric.Percentile("load_time", 95).Where(isError);
    AnalyticsResult Run(QuerySource source, TimeInterval interval, params string[] groupBy) => Query(q =>
    {
      q.Source = source;
      q.Interval = interval;
      q.GroupBy.AddRange(groupBy);
      q.Metrics.AddRange([Metric.Events, errors, errorRate, okLoad, errorP95]);
    });
    foreach (var (interval, groupBy) in new[] { (TimeInterval.None, Array.Empty<string>()), (TimeInterval.Hour, Array.Empty<string>()), (TimeInterval.Day, ["tenant"]) })
    {
      var rollups = Run(QuerySource.Rollups, interval, groupBy);
      Assert.That(rollups.Plan.UsedRawEvents, Is.False);
      Assert.That(Describe(rollups), Is.EqualTo(Describe(Run(QuerySource.RawEvents, interval, groupBy))), $"{interval} by [{string.Join(",", groupBy)}]");
    }
    var byTenant = Run(QuerySource.Auto, TimeInterval.None, "tenant");
    var filtered = Query(q =>
    {
      q.GroupBy.Add("tenant");
      q.Filters.Add(isError);
      q.Metrics.AddRange([Metric.Events, Metric.Percentile("load_time", 95)]);
    });
    var errorCount = requests.Count(x => x.Status == "500");
    Assert.Multiple(() =>
    {
      Assert.That(byTenant.Totals[errors], Is.EqualTo(errorCount));
      Assert.That(byTenant.Totals[errorRate], Is.EqualTo((double)errorCount / requests.Count));
      Assert.That(byTenant.Totals[okLoad], Is.EqualTo(requests.Where(x => x.Status == "200").Sum(x => x.LoadTime)).Within(1e-6));
      foreach (var row in filtered.Rows)
      {
        var same = byTenant.Rows.Single(x => x["tenant"] == row["tenant"]);
        Assert.That(same[errors], Is.EqualTo(row[Metric.Events]));
        Assert.That(same[errorP95], Is.EqualTo(row[Metric.Percentile("load_time", 95)]));
      }
    });
  }

  [Test]
  public void ConditionalSessionAndPageFlowMetrics()
  {
    engine = TestEngine.Create(directory);
    void View(string page, string ip, int minute) => engine.Track(new AnalyticsEvent(StandardEvents.PageView)
    {
      Url = "https://mysite.com" + page,
      IpAddress = ip,
      UserAgent = UserAgents.ChromeWindows,
      Timestamp = Utc(Day0.AddMinutes(minute))
    });
    View("/", "10.0.0.1", 0);
    View("/pricing", "10.0.0.1", 1);
    View("/", "10.0.0.2", 5);
    View("/pricing", "10.0.0.3", 6);
    engine.Flush();
    var german = Filter.Equal(StandardDimensions.Country, "DE");
    var home = Filter.Equal(StandardDimensions.Page, "/");
    foreach (var source in new[] { QuerySource.Rollups, QuerySource.RawEvents })
    {
      var conditional = engine.Query(new AnalyticsQuery
      {
        Range = Range(Day0, Day0.AddDays(1)),
        Source = source,
        Metrics = { Metric.Sessions, Metric.Sessions.Where(german), Metric.BounceRate.Where(german), Metric.Exits.Where(home) }
      });
      var sessionsInGermany = engine.Query(new AnalyticsQuery
      {
        Range = Range(Day0, Day0.AddDays(1)),
        Source = source,
        Filters = { german },
        Metrics = { Metric.Sessions, Metric.BounceRate }
      });
      Assert.Multiple(() =>
      {
        Assert.That(conditional.Totals[Metric.Sessions], Is.EqualTo(3), source.ToString());
        Assert.That(conditional.Totals[Metric.Sessions.Where(german)], Is.EqualTo(sessionsInGermany.Totals[Metric.Sessions]).And.EqualTo(2));
        Assert.That(conditional.Totals[Metric.BounceRate.Where(german)], Is.EqualTo(sessionsInGermany.Totals[Metric.BounceRate]));
        Assert.That(conditional.Totals[Metric.Exits.Where(home)], Is.EqualTo(1), "one session ended on the home page");
      });
    }
    var exception = Assert.Throws<AnalyticsQueryException>(() => engine.Query(new AnalyticsQuery
    {
      Metrics = { Metric.Sessions.Where(Filter.Equal("plan", "pro")) }
    }));
    Assert.That(exception.Message, Does.Contain("'plan'"));
  }

  [Test]
  public void ConditionalMetricKeys()
  {
    var ok = Filter.Equal("status", "200");
    var tricky = Filter.Matches("page", "^/(a|b),\"x\"\\d$");
    var formula = (Metric.Sum("load_time") / Metric.Events).Where(ok);
    Assert.Multiple(() =>
    {
      Assert.That(Metric.Events.Where(ok).Key, Is.EqualTo("where(events,\"status==200\")"));
      Assert.That(formula.Key, Is.EqualTo("div(where(sum:load_time,\"status==200\"),where(events,\"status==200\"))"));
      Assert.That(Metric.Events.Where(ok).Where(tricky), Is.EqualTo(Metric.Events.Where(tricky, ok)));
      Assert.That(Metric.Parse(Metric.Events.Where(ok, tricky).Key), Is.EqualTo(Metric.Events.Where(ok, tricky)));
      Assert.That(Metric.Parse(formula.Key), Is.EqualTo(formula));
      Assert.That(Metric.Constant(2).Where(ok), Is.EqualTo(Metric.Constant(2)));
      Assert.That(Metric.Events.Where(), Is.SameAs(Metric.Events));
      Assert.Throws<FormatException>(() => Metric.Parse("where(events,status==200)"));
    });
  }

  [Test]
  public void HistogramsFromRollupsEqualRawEvents()
  {
    engine = TestEngine.Create(directory, ConfigureAggregates);
    var requests = TrackRequests();
    double[] edges = [double.NegativeInfinity, 100, 150, 250, 500, 1000, double.PositiveInfinity];
    var bins = Metric.Histogram("load_time", edges);
    var everything = Metric.CountBetween("load_time", 0, 1e9);
    AnalyticsResult Run(QuerySource source) => Query(q =>
    {
      q.Source = source;
      q.Interval = TimeInterval.Hours(12);
      q.Metrics.AddRange(bins);
      q.Metrics.Add(everything);
      q.Metrics.Add(Metric.Count("load_time"));
    });
    var rollups = Run(QuerySource.Rollups);
    Assert.That(Describe(rollups), Is.EqualTo(Describe(Run(QuerySource.RawEvents))));
    Assert.Multiple(() =>
    {
      Assert.That(bins.Sum(x => rollups.Totals[x]), Is.EqualTo(requests.Count), "bins over the whole line count every value");
      Assert.That(rollups.Totals[everything], Is.EqualTo(requests.Count), "a range that holds every value is exact");
      for (var i = 0; i < bins.Length; ++i)
      {
        var (lower, upper) = (edges[i], edges[i + 1]);
        var exact = requests.Count(x => x.LoadTime >= lower && x.LoadTime < upper);
        // Only values within 1% of an edge may land in the neighboring bin.
        var nearEdge = requests.Count(x => Math.Abs(x.LoadTime - lower) <= Math.Abs(lower) * 0.0101 ||
            Math.Abs(x.LoadTime - upper) <= Math.Abs(upper) * 0.0101);
        Assert.That(rollups.Totals[bins[i]], Is.EqualTo(exact).Within(nearEdge), $"[{lower}, {upper})");
      }
      Assert.That(Metric.Parse(bins[0].Key), Is.EqualTo(bins[0]));
      Assert.That(bins[^1].Key, Is.EqualTo("count_between(load_time,1000,Infinity)"));
      Assert.Throws<ArgumentOutOfRangeException>(() => Metric.CountBetween("x", 5, 5));
      Assert.Throws<ArgumentException>(() => Metric.Histogram("x", [1]));
    });
  }

  [Test]
  public void LagAndLead()
  {
    engine = TestEngine.Create(directory);
    double[] revenue = [10, 30, 20, 40];
    for (var day = 0; day < 4; ++day)
      engine.Track(new AnalyticsEvent("purchase") { VisitorId = "a", Timestamp = Utc(Day0.AddDays(day)), Values = { ["revenue"] = revenue[day] } });
    engine.Flush();
    var sum = Metric.Sum("revenue");
    var previous = Metric.Lag(sum);
    var twoBack = Metric.Lag(sum, 2);
    var next = Metric.Lead(sum);
    var growth = sum / previous - 1;
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "purchase",
      Range = Range(Day0, Day0.AddDays(4)),
      Interval = TimeInterval.Day,
      Metrics = { sum, previous, twoBack, next, growth }
    });
    double[] Column(Metric metric) => result.Rows.Select(x => x[metric]).ToArray();
    Assert.Multiple(() =>
    {
      Assert.That(Column(previous), Is.EqualTo(new double[] { 0, 10, 30, 20 }));
      Assert.That(Column(twoBack), Is.EqualTo(new double[] { 0, 0, 10, 30 }));
      Assert.That(Column(next), Is.EqualTo(new double[] { 30, 20, 40, 0 }));
      Assert.That(Column(growth), Is.EqualTo(new double[] { -1, 2, 20.0 / 30 - 1, 1 }).Within(1e-12));
      Assert.That(result.Totals[previous], Is.EqualTo(100));
      Assert.That(previous.Key, Is.EqualTo("lag(sum:revenue,1)"));
      Assert.That(Metric.Parse("lag(sum:revenue)"), Is.EqualTo(previous));
      Assert.Throws<ArgumentOutOfRangeException>(() => Metric.Lead(sum, 0));
    });
  }

  [Test]
  public void LimitPerBucketReturnsTheTopRowsOfEachBucket()
  {
    engine = TestEngine.Create(directory, ConfigureAggregates);
    var requests = TrackRequests();
    var slowest = Metric.Max("load_time");
    var result = Query(q =>
    {
      q.Interval = TimeInterval.Day;
      q.GroupBy.Add("status");
      q.Metrics.Add(slowest);
      q.OrderBy.Add(Order.ByBucket());
      q.OrderBy.Add(Order.By(slowest));
      q.LimitPerBucket = 1;
    });
    var expected = requests.GroupBy(x => x.Time.Date).Select(day => day.MaxBy(x => x.LoadTime)).ToList();
    Assert.Multiple(() =>
    {
      Assert.That(result.Rows, Has.Count.EqualTo(2));
      Assert.That(result.TotalRows, Is.EqualTo(2));
      Assert.That(result.Rows.Select(x => x[slowest]), Is.EqualTo(expected.Select(x => x.LoadTime)));
      Assert.That(result.Rows.Select(x => x["status"]), Is.EqualTo(expected.Select(x => x.Status)), "the status of the slowest request");
      Assert.Throws<AnalyticsQueryException>(() => Query(q => q.LimitPerBucket = 0));
    });
  }

  #endregion

  #region Having and numeric filters

  [Test]
  public void HavingFiltersRows()
  {
    engine = TestEngine.Create(directory, ConfigureAggregates);
    TrackRequests(300);
    var byTenant = Query(q =>
    {
      q.GroupBy.Add("tenant");
      q.Metrics.Add(Metric.Events);
    });
    var threshold = byTenant.Rows.Select(x => x[Metric.Events]).Order().ElementAt(3);
    var filtered = Query(q =>
    {
      q.GroupBy.Add("tenant");
      q.Metrics.Add(Metric.Events);
      q.Having.Add(MetricFilter.Parse("events>=" + threshold.ToString(CultureInfo.InvariantCulture)));
      q.Having.Add(MetricFilter.GreaterThan(Metric.Percentile("load_time", 50), 0));
    });
    Assert.Multiple(() =>
    {
      Assert.That(filtered.Rows.Select(x => x[Metric.Events]), Is.All.GreaterThanOrEqualTo(threshold));
      Assert.That(filtered.TotalRows, Is.EqualTo(byTenant.Rows.Count(x => x[Metric.Events] >= threshold)));
      Assert.That(filtered.Totals[Metric.Events], Is.EqualTo(byTenant.Totals[Metric.Events]), "totals are not affected");
    });
  }

  [Test]
  public void MetricFiltersParse()
  {
    Assert.Multiple(() =>
    {
      Assert.That(MetricFilter.Parse("pageviews>100").ToString(), Is.EqualTo("pageviews>100"));
      Assert.That(MetricFilter.Parse("p95:load_time >= 2000").Operator, Is.EqualTo(ComparisonOperator.GreaterThanOrEqual));
      Assert.That(MetricFilter.Parse("div(sum:a,events)<=-1.5").Value, Is.EqualTo(-1.5));
      Assert.That(MetricFilter.Parse("visitors!=0").Operator, Is.EqualTo(ComparisonOperator.NotEqual));
      Assert.That(MetricFilter.Parse("visitors==3").Operator, Is.EqualTo(ComparisonOperator.Equal));
      Assert.That(MetricFilter.Parse("visitors<3").Operator, Is.EqualTo(ComparisonOperator.LessThan));
      Assert.Throws<FormatException>(() => MetricFilter.Parse("visitors"));
      Assert.Throws<FormatException>(() => MetricFilter.Parse("visitors>abc"));
    });
  }

  [Test]
  public void NumericFiltersCompareNumbers()
  {
    engine = TestEngine.Create(directory, ConfigureAggregates);
    var requests = TrackRequests(300);
    engine.Track(new AnalyticsEvent("request") { VisitorId = "x", Timestamp = Utc(Day0.AddHours(1)), Dimensions = { ["status"] = "n/a" } });
    engine.Flush();
    var errors = requests.Count(x => int.Parse(x.Status, CultureInfo.InvariantCulture) >= 500);
    AnalyticsResult Run(QuerySource source, Filter filter) => Query(q =>
    {
      q.Source = source;
      q.Filters.Add(filter);
      q.Metrics.Add(Metric.Events);
    });
    Assert.Multiple(() =>
    {
      foreach (var source in new[] { QuerySource.Rollups, QuerySource.RawEvents })
      {
        Assert.That(Run(source, Filter.GreaterThanOrEqual("status", 500)).Totals[Metric.Events], Is.EqualTo(errors), source.ToString());
        Assert.That(Run(source, Filter.Parse("status>=500")).Totals[Metric.Events], Is.EqualTo(errors), source.ToString());
        Assert.That(Run(source, Filter.Parse("status<300")).Totals[Metric.Events], Is.EqualTo(requests.Count(x => x.Status == "200")));
      }
      Assert.That(Filter.Parse("status>=500").Operator, Is.EqualTo(FilterOperator.GreaterThanOrEqual));
      Assert.That(Filter.Parse("status>=500").ToString(), Is.EqualTo("status>=500"));
      Assert.That(Filter.LessThan("x", 1.5).IsMatch("1.25"), Is.True);
      Assert.That(Filter.LessThan("x", 1.5).IsMatch("abc"), Is.False);
      Assert.That(Filter.LessThan("x", 1.5).IsMatch(null), Is.False);
      Assert.Throws<FormatException>(() => Filter.Parse("status>=a"));
      Assert.Throws<ArgumentException>(() => new Filter("status", FilterOperator.GreaterThan, "1", "2"));
    });
  }

  #endregion
}
