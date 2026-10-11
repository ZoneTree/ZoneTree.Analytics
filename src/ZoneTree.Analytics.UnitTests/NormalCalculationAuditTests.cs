using ZoneTree.Analytics.Query;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Release audit of ordinary workloads. Expectations come from an input ledger, decimal
/// arithmetic and exhaustive subsequence enumeration, not from another engine query.
/// </summary>
public sealed class NormalCalculationAuditTests : DirectoryTest
{
  static readonly DateTime Start = new(2025, 1, 27, 0, 0, 0, DateTimeKind.Utc);
  static readonly QuerySource[] Sources = [QuerySource.RawEvents, QuerySource.Rollups, QuerySource.Auto];

  sealed record Hit(string Visitor, string Country, DateTime Time, string Event, string Page, bool NonInteractive = false);
  sealed record Visit(string Visitor, string Country, DateTime Time, List<Hit> Hits)
  {
    public Hit[] Pages => Hits.Where(h => h.Event == StandardEvents.PageView).ToArray();
    public bool Bounce => Hits.Count(h => !h.NonInteractive) <= 1;
    public double Seconds => (Hits[^1].Time - Time).TotalSeconds;
  }

  static List<Visit> Traffic()
  {
    var visits = new List<Visit>();
    for (var day = 0; day < 14; ++day)
      for (var visitor = 0; visitor < 24; ++visitor)
      {
        if ((day + visitor) % 4 == 0)
          continue;
        for (var repeat = 0; repeat < 1 + ((visitor + day) % 5 == 0 ? 1 : 0); ++repeat)
        {
          var time = Start.AddDays(day).AddHours(7 + repeat * 6).AddMinutes(visitor * 17 + 1).AddSeconds(13);
          var id = "visitor-" + visitor;
          var country = visitor % 3 == 0 ? "US" : "DE";
          var hits = new List<Hit>();
          var views = 1 + (visitor + day + repeat) % 4;
          for (var page = 0; page < views; ++page)
            hits.Add(new Hit(id, country, time.AddMinutes(page * 2), StandardEvents.PageView,
                new[] { "/", "/products", "/pricing", "/checkout" }[(page + visitor) % 4]));
          var last = hits[^1].Time;
          if ((visitor + day + repeat) % 3 == 0)
          {
            hits.Add(new Hit(id, country, last.AddMinutes(1), "purchase", "/checkout"));
            if (visitor % 2 == 0)
              hits.Add(new Hit(id, country, last.AddMinutes(2), "purchase", "/checkout"));
          }
          hits.Add(new Hit(id, country, hits[^1].Time.AddMinutes(3), "heartbeat", null, true));
          visits.Add(new Visit(id, country, time, hits));
        }
      }
    return visits;
  }

  static void ConfigureTraffic(AnalyticsOptions options, int workers)
  {
    options.Ingestion.WorkerCount = workers;
    options.Queries.MaxDegreeOfParallelism = workers;
    options.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
    options.Rollups.Definitions.Add(new RollupDefinition("country", "page"));
  }

  static void TrackTraffic(AnalyticsEngine engine, List<Visit> visits)
  {
    var count = 0;
    foreach (var hit in visits.SelectMany(v => v.Hits).OrderBy(h => h.Time))
    {
      engine.Track(new AnalyticsEvent(hit.Event)
      {
        VisitorId = hit.Visitor, Timestamp = Utc(hit.Time), Url = hit.Page,
        NonInteractive = hit.NonInteractive, Dimensions = { ["country"] = hit.Country }
      });
      if (++count % 75 == 0)
        engine.Flush();
    }
    // Traffic for a different site must never enter the default site's totals.
    engine.Track(new AnalyticsEvent("pageview")
    {
      Site = "other", VisitorId = "other", Timestamp = Utc(Start.AddDays(8).AddHours(10)), Url = "/noise"
    });
    engine.Flush();
  }

  static DateTime Bucket(DateTime time, string interval) => interval switch
  {
    "none" => DateTime.MinValue,
    "hour" => new DateTime(time.Year, time.Month, time.Day, time.Hour, 0, 0, DateTimeKind.Utc),
    "day" => time.Date,
    "week" => time.Date.AddDays(-((int)time.DayOfWeek + 6) % 7),
    "month" => new DateTime(time.Year, time.Month, 1, 0, 0, 0, DateTimeKind.Utc),
    _ => throw new ArgumentException(interval)
  };

  static readonly Metric[] TrafficMetrics =
  [
    Metric.PageViews, Metric.Visitors, Metric.Sessions, Metric.Bounces, Metric.BounceRate,
    Metric.SessionDuration, Metric.AverageSessionDuration, Metric.PageViewsPerSession,
    Metric.Entrances, Metric.Exits, Metric.ExitRate, Metric.EventsOf("purchase"),
    Metric.VisitorsOf("purchase"), Metric.ConversionRate("purchase")
  ];

  static void CheckTrafficRow(AnalyticsRow row, List<Visit> visits, DateTime from, DateTime to,
      string country, string interval, DateTime? bucket, string label)
  {
    bool Included(DateTime time) => time >= from && time < to &&
        (!bucket.HasValue || Bucket(time, interval) == bucket.Value);
    var selected = visits.Where(v => country == null || v.Country == country).ToArray();
    var hits = selected.SelectMany(v => v.Hits).Where(h => Included(h.Time)).ToArray();
    var pages = hits.Where(h => h.Event == StandardEvents.PageView).ToArray();
    var sessions = selected.Where(v => Included(v.Time)).ToArray();
    var goals = hits.Where(h => h.Event == "purchase").ToArray();
    var unique = pages.Select(h => h.Visitor).Distinct().Count();
    var converted = goals.Select(h => h.Visitor).Distinct().Count();
    var entrances = selected.Count(v => Included(v.Pages[0].Time));
    var exits = selected.Count(v => Included(v.Pages[^1].Time));
    var bounces = sessions.Count(v => v.Bounce);
    var seconds = sessions.Sum(v => v.Seconds);
    var expected = new double[]
    {
      pages.Length, unique, sessions.Length, bounces, sessions.Length == 0 ? 0 : (double)bounces / sessions.Length,
      seconds, sessions.Length == 0 ? 0 : seconds / sessions.Length,
      sessions.Length == 0 ? 0 : (double)sessions.Sum(v => v.Pages.Length) / sessions.Length,
      entrances, exits, pages.Length == 0 ? 0 : (double)exits / pages.Length,
      goals.Length, converted, unique == 0 ? 0 : Math.Min(1, (double)converted / unique)
    };
    for (var i = 0; i < TrafficMetrics.Length; ++i)
      Assert.That(row[TrafficMetrics[i]], Is.EqualTo(expected[i]).Within(1e-9), label + " " + TrafficMetrics[i]);
  }

  [TestCase(1, false)]
  [TestCase(3, true)]
  public void OrdinaryTrafficMatchesTheLedgerAcrossSourcesIntervalsFiltersAndReopen(int workers, bool reopen)
  {
    var visits = Traffic();
    using (var writer = TestEngine.Create(directory, o => ConfigureTraffic(o, workers)))
    {
      TrackTraffic(writer, visits);
      if (reopen)
        writer.Compact();
      else
      {
        CheckTrafficQueries(writer, visits);
        return;
      }
    }
    using var reader = TestEngine.Create(directory, o => ConfigureTraffic(o, workers));
    CheckTrafficQueries(reader, visits);
  }

  static void CheckTrafficQueries(AnalyticsEngine engine, List<Visit> visits)
  {
    foreach (var source in Sources)
      foreach (var interval in new[] { "none", "hour", "day", "week", "month" })
        foreach (var country in new[] { null, "DE" })
        {
          var from = Start.AddDays(7);
          var to = Start.AddDays(14);
          var query = new AnalyticsQuery
          {
            Range = Range(from, to), Source = source, Interval = TimeInterval.Parse(interval),
            Fill = FillMode.None, GroupBy = { "country" }
          };
          query.Metrics.AddRange(TrafficMetrics);
          if (country != null)
            query.Filters.Add(Filter.Equal("country", country));
          var result = engine.Query(query);
          var label = $"{source}/{interval}/{country ?? "all"}";
          Assert.That(result.Plan.IsComplete, Is.True, label);
          // No implicit omissions or extra rows: the keys are independently derived from all sources.
          var expectedKeys = visits.Where(v => country == null || v.Country == country)
              .SelectMany(v => v.Hits.Where(h => h.Event is "pageview" or "purchase")
                  .Select(h => (h.Time, v.Country)).Append((v.Time, v.Country)))
              .Where(h => h.Time >= from && h.Time < to)
              .Select(h => (Bucket(h.Time, interval), h.Country)).Distinct().ToHashSet();
          Assert.That(result.Rows.Select(r => (r.Bucket?.UtcDateTime ?? DateTime.MinValue, r["country"])).ToHashSet(),
              Is.EquivalentTo(expectedKeys), label);
          foreach (var row in result.Rows)
            CheckTrafficRow(row, visits, from, to, row["country"], interval,
                row.Bucket?.UtcDateTime ?? DateTime.MinValue, label);
          CheckTrafficRow(result.Totals, visits, from, to, country, interval, null, label + " total");
        }
    // A typical clock range needs raw boundary minutes and rollup interior hours.
    var partialFrom = Start.AddDays(8).AddHours(9).AddMinutes(15).AddSeconds(12);
    var partialTo = Start.AddDays(11).AddHours(15).AddMinutes(45).AddSeconds(27);
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Auto })
    {
      var query = new AnalyticsQuery { Range = Range(partialFrom, partialTo), Source = source };
      query.Metrics.AddRange(TrafficMetrics);
      var result = engine.Query(query);
      CheckTrafficRow(result.Totals, visits, partialFrom, partialTo, null, "none", null, source + " partial");
      Assert.That(result.Plan.IsComplete, Is.True);
      if (source == QuerySource.Auto)
      {
        Assert.That(result.Plan.UsedRollups, Is.True);
        Assert.That(result.Plan.UsedRawEvents, Is.True);
      }
    }
    Assert.That(engine.GetStatistics().Errors, Is.Zero);
  }

  sealed record Reading(DateTime Time, string Tenant, string Status, string Visitor, string Product, decimal? Cost, double Load);

  static void ConfigureReadings(AnalyticsOptions options, int workers)
  {
    options.Ingestion.WorkerCount = workers;
    options.Queries.MaxDegreeOfParallelism = workers;
    options.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
    options.Rollups.ValueAggregates["cost"] = ValueAggregates.All;
    options.Rollups.ValueAggregates["load"] = ValueAggregates.All;
    options.Rollups.DistinctDimensions.Add("product");
    foreach (var dims in new[] { new[] { "tenant" }, new[] { "status" }, new[] { "tenant", "status" } })
      options.Rollups.Definitions.Add(new RollupDefinition(dims));
  }

  static List<Reading> Readings() => Enumerable.Range(0, 720).Select(i => new Reading(
      Start.AddMinutes(i * 11 + 3), "t" + i % 5, i % 7 == 0 ? "500" : "200", "u" + i % 37,
      i % 11 == 0 ? null : "p" + i % 13, i % 9 == 0 ? null : (i % 41 - 9) * 2.5m,
      new[] { 100, 200, 400, 800 }[i % 4] + i % 17)).ToList();

  static readonly Metric ErrorCount = Metric.Events.Where(Filter.GreaterThanOrEqual("status", 500));
  static readonly Metric ErrorCost = Metric.Sum("cost").Where(Filter.GreaterThanOrEqual("status", 500));
  static readonly Metric[] ReadingMetrics =
  [
    Metric.Events, Metric.Visitors, Metric.Sum("cost"), Metric.Count("cost"), Metric.Average("cost"),
    Metric.Min("cost"), Metric.Max("cost"), Metric.Variance("cost"), Metric.StandardDeviation("cost"),
    Metric.First("cost"), Metric.Last("cost"), Metric.Distinct("product"), ErrorCount, ErrorCost,
    Metric.Percentile("load", 0), Metric.Median("load"), Metric.Percentile("load", 95), Metric.Percentile("load", 100),
    Metric.CountBetween("load", 0, 150), Metric.CountBetween("load", 150, 300),
    Metric.CountBetween("load", 300, 600), Metric.CountBetween("load", 600, double.PositiveInfinity),
    Metric.Sum("cost") / Metric.Count("cost"), Metric.Events - ErrorCount, Metric.Sum("cost") * 1.2 + 5
  ];

  static void CheckReadingRow(AnalyticsRow row, IEnumerable<Reading> input, string label)
  {
    var selected = input.OrderBy(r => r.Time).ToArray();
    var values = selected.Where(r => r.Cost.HasValue).Select(r => r.Cost.Value).ToArray();
    var sum = values.Sum();
    var mean = values.Length == 0 ? 0 : sum / values.Length;
    var variance = values.Length == 0 ? 0 : values.Sum(v => (v - mean) * (v - mean)) / values.Length;
    var loads = selected.Select(r => r.Load).Order().ToArray();
    var error = selected.Where(r => r.Status == "500").ToArray();
    var expected = new double[]
    {
      selected.Length, selected.Select(r => r.Visitor).Distinct().Count(), (double)sum, values.Length, (double)mean,
      values.Length == 0 ? 0 : (double)values.Min(), values.Length == 0 ? 0 : (double)values.Max(),
      (double)variance, Math.Sqrt((double)variance), values.Length == 0 ? 0 : (double)values[0],
      values.Length == 0 ? 0 : (double)values[^1],
      selected.Select(r => r.Product).Where(p => p != null).Distinct().Count(), error.Length,
      (double)error.Sum(r => r.Cost ?? 0), loads.Length == 0 ? 0 : loads[0],
      loads.Length == 0 ? 0 : loads[(loads.Length - 1) / 2],
      loads.Length == 0 ? 0 : loads[(int)Math.Floor((loads.Length - 1) * .95)], loads.Length == 0 ? 0 : loads[^1],
      loads.Count(v => v < 150), loads.Count(v => v >= 150 && v < 300),
      loads.Count(v => v >= 300 && v < 600), loads.Count(v => v >= 600),
      (double)mean, selected.Length - error.Length, (double)sum * 1.2 + 5
    };
    for (var i = 0; i < ReadingMetrics.Length; ++i)
    {
      var tolerance = i is 15 or 16 ? Math.Abs(expected[i]) * .0101 : 1e-8;
      Assert.That(row[ReadingMetrics[i]], Is.EqualTo(expected[i]).Within(tolerance), label + " " + ReadingMetrics[i]);
    }
  }

  [TestCase(1, false)]
  [TestCase(3, true)]
  public void OrdinaryNumericAggregatesMatchDecimalArithmeticAndSortedSamples(int workers, bool reopen)
  {
    var readings = Readings();
    using (var writer = TestEngine.Create(directory, o => ConfigureReadings(o, workers)))
    {
      var count = 0;
      foreach (var r in readings)
      {
        var ev = new AnalyticsEvent("request")
        {
          VisitorId = r.Visitor, Timestamp = Utc(r.Time),
          Dimensions = { ["tenant"] = r.Tenant, ["status"] = r.Status }, Values = { ["load"] = r.Load }
        };
        if (r.Product != null)
          ev.Dimensions["product"] = r.Product;
        if (r.Cost.HasValue)
          ev.Values["cost"] = (double)r.Cost.Value;
        writer.Track(ev);
        if (++count % 41 == 0)
          writer.Flush();
      }
      writer.Flush();
      if (reopen)
        writer.Compact();
      else
      {
        CheckReadingQueries(writer, readings);
        return;
      }
    }
    using var reader = TestEngine.Create(directory, o => ConfigureReadings(o, workers));
    CheckReadingQueries(reader, readings);
  }

  static void CheckReadingQueries(AnalyticsEngine engine, List<Reading> readings)
  {
    foreach (var source in Sources)
      foreach (var interval in new[] { "none", "hour", "day" })
        foreach (var filtered in new[] { false, true })
        {
          var from = Start.AddHours(2).AddMinutes(17);
          var to = Start.AddDays(5).AddHours(8).AddMinutes(43);
          var query = new AnalyticsQuery
          {
            Event = "request", Range = Range(from, to), Source = source,
            Interval = TimeInterval.Parse(interval), Fill = FillMode.None, GroupBy = { "tenant", "status" }
          };
          query.Metrics.AddRange(ReadingMetrics);
          if (filtered)
            query.Filters.Add(Filter.Equal("tenant", "t2"));
          var selected = readings.Where(r => r.Time >= from && r.Time < to && (!filtered || r.Tenant == "t2")).ToArray();
          var result = engine.Query(query);
          var groups = selected.GroupBy(r => (Bucket(r.Time, interval), r.Tenant, r.Status)).ToDictionary(g => g.Key, g => g.ToArray());
          Assert.That(result.Plan.IsComplete, Is.True);
          Assert.That(result.Rows.Count, Is.EqualTo(groups.Count));
          foreach (var row in result.Rows)
          {
            var key = (row.Bucket?.UtcDateTime ?? DateTime.MinValue, row["tenant"], row["status"]);
            Assert.That(groups.ContainsKey(key), Is.True);
            CheckReadingRow(row, groups[key], $"{source}/{interval}/{filtered}/{key}");
          }
          CheckReadingRow(result.Totals, selected, $"{source}/{interval}/{filtered}/total");
        }
    Assert.That(engine.GetStatistics().Errors, Is.Zero);
  }

  static Visit[] SelectVisits(List<Visit> visits, DateTime from, DateTime to, string country = null) =>
      visits.Where(v => v.Time >= from && v.Time < to && (country == null || v.Country == country)).ToArray();

  static void CheckSummary(WebSummary summary, Visit[] visits)
  {
    Assert.That(summary, Is.Not.Null);
    Assert.Multiple(() =>
    {
      Assert.That(summary.PageViews, Is.EqualTo(visits.Sum(v => v.Pages.Length)));
      Assert.That(summary.Visitors, Is.EqualTo(visits.Select(v => v.Visitor).Distinct().Count()));
      Assert.That(summary.Sessions, Is.EqualTo(visits.Length));
      Assert.That(summary.BounceRate, Is.EqualTo(visits.Length == 0 ? 0 : (double)visits.Count(v => v.Bounce) / visits.Length).Within(1e-10));
      Assert.That(summary.AverageSessionDuration?.TotalSeconds, Is.EqualTo(visits.Length == 0 ? 0 : visits.Average(v => v.Seconds)).Within(1e-6));
      Assert.That(summary.PageViewsPerSession, Is.EqualTo(visits.Length == 0 ? 0 : visits.Average(v => v.Pages.Length)).Within(1e-10));
    });
  }

  [TestCase(1)]
  [TestCase(3)]
  public void ReadyMadeWebReportsAndComparisonsMatchTheTrafficLedger(int workers)
  {
    var visits = Traffic();
    using var engine = TestEngine.Create(directory, o => ConfigureTraffic(o, workers));
    TrackTraffic(engine, visits);
    var from = Start.AddDays(7);
    var to = Start.AddDays(14);
    foreach (var country in new[] { null, "DE" })
    {
      var current = SelectVisits(visits, from, to, country);
      var previous = SelectVisits(visits, Start, from, country);
      var request = new WebReportRequest
      {
        Range = Range(from, to), Compare = ComparisonPeriod.PreviousPeriod, Limit = null
      };
      if (country != null)
        request.Filters.Add(Filter.Equal("country", country));
      var summary = engine.Web.GetSummary(request);
      Assert.That(summary.Plan.IsComplete, Is.True);
      CheckSummary(summary.Summary, current);
      CheckSummary(summary.Summary.Previous, previous);

      var series = engine.Web.GetTimeseries(request, TimeInterval.Day);
      Assert.That(series.Rows.Count, Is.EqualTo(7));
      foreach (var point in series.Rows)
      {
        var day = point.Bucket.UtcDateTime;
        CheckSummary(point.Summary, current.Where(v => v.Time.Date == day).ToArray());
        CheckSummary(point.Summary.Previous, previous.Where(v => v.Time.Date == day.AddDays(-7)).ToArray());
      }

      var breakdown = engine.Web.GetBreakdown(request, "country");
      Assert.That(breakdown.TotalRows, Is.EqualTo(country == null ? 2 : 1));
      foreach (var row in breakdown.Rows)
      {
        var selected = current.Where(v => v.Country == row.Value).ToArray();
        var prior = previous.Where(v => v.Country == row.Value).ToArray();
        Assert.Multiple(() =>
        {
          Assert.That(row.PageViews, Is.EqualTo(selected.Sum(v => v.Pages.Length)));
          Assert.That(row.Visitors, Is.EqualTo(selected.Select(v => v.Visitor).Distinct().Count()));
          Assert.That(row.Sessions, Is.EqualTo(selected.Length));
          Assert.That(row.BounceRate, Is.EqualTo((double)selected.Count(v => v.Bounce) / selected.Length).Within(1e-10));
          Assert.That(row.AverageSessionDuration?.TotalSeconds, Is.EqualTo(selected.Average(v => v.Seconds)).Within(1e-6));
          Assert.That(row.VisitorShare, Is.EqualTo((double)selected.Select(v => v.Visitor).Distinct().Count() /
              current.Select(v => v.Visitor).Distinct().Count()).Within(1e-10));
          Assert.That(row.Previous.PageViews, Is.EqualTo(prior.Sum(v => v.Pages.Length)));
        });
      }

      var pages = engine.Web.GetPages(request);
      Assert.That(pages.Rows.Count, Is.EqualTo(4));
      foreach (var row in pages.Rows)
      {
        var selected = current.SelectMany(v => v.Pages).Where(h => h.Page == row.Page).ToArray();
        var exits = current.Count(v => v.Pages[^1].Page == row.Page);
        Assert.Multiple(() =>
        {
          Assert.That(row.PageViews, Is.EqualTo(selected.Length));
          Assert.That(row.Visitors, Is.EqualTo(selected.Select(h => h.Visitor).Distinct().Count()));
          Assert.That(row.Entrances, Is.EqualTo(current.Count(v => v.Pages[0].Page == row.Page)));
          Assert.That(row.Exits, Is.EqualTo(exits));
          Assert.That(row.ExitRate, Is.EqualTo((double)exits / selected.Length).Within(1e-10));
          Assert.That(row.Previous.PageViews, Is.EqualTo(previous.SelectMany(v => v.Pages).Count(h => h.Page == row.Page)));
        });
      }
      Assert.That(pages.Rows.Select(r => r.PageViews), Is.Ordered.Descending);
      var goals = engine.Web.GetGoals(request);
      Assert.That(goals.Rows.Select(r => r.Name), Is.EquivalentTo(new[] { "purchase", "heartbeat" }));
      foreach (var row in goals.Rows)
      {
        var hits = current.SelectMany(v => v.Hits).Where(h => h.Event == row.Name).ToArray();
        var count = hits.Select(h => h.Visitor).Distinct().Count();
        Assert.Multiple(() =>
        {
          Assert.That(row.Events, Is.EqualTo(hits.Length));
          Assert.That(row.Visitors, Is.EqualTo(count));
          Assert.That(row.ConversionRate, Is.EqualTo((double)count / current.Select(v => v.Visitor).Distinct().Count()).Within(1e-10));
        });
      }
      // Paging must not change shares or the number of rows before paging.
      request.Limit = 1;
      request.Offset = 1;
      var paged = engine.Web.GetPages(request);
      Assert.That(paged.Rows.Select(r => r.Page), Is.EqualTo(pages.Rows.Skip(1).Take(1).Select(r => r.Page)));
      Assert.That(paged.TotalRows, Is.EqualTo(4));
    }
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  [TestCase(QuerySource.Auto)]
  public void ComparisonRowsAndTotalsMatchIndependentPriorPeriodData(QuerySource source)
  {
    var visits = Traffic();
    using var engine = TestEngine.Create(directory, o => ConfigureTraffic(o, 2));
    TrackTraffic(engine, visits);
    foreach (var interval in new[] { "none", "hour", "day" })
    {
      var query = new AnalyticsQuery
      {
        Range = Range(Start.AddDays(7), Start.AddDays(14)), Source = source,
        Interval = TimeInterval.Parse(interval), Fill = FillMode.Zero, GroupBy = { "country" },
        Compare = ComparisonPeriod.PreviousPeriod
      };
      query.Metrics.AddRange(TrafficMetrics);
      var result = engine.Query(query);
      Assert.That(result.Comparison.Plan.IsComplete, Is.True);
      CheckTrafficRow(result.Totals.Previous, visits, Start, Start.AddDays(7), null, interval, null, "prior total");
      foreach (var row in result.Rows)
      {
        Assert.That(row.Previous, Is.Not.Null);
        CheckTrafficRow(row.Previous, visits, Start, Start.AddDays(7), row["country"], interval,
            row.Bucket?.UtcDateTime.AddDays(-7) ?? DateTime.MinValue, "prior row");
      }
    }
  }

  [TestCase(1)]
  [TestCase(3)]
  public void BehavioralSegmentsAndTheirComplementsMatchSelectedLedgerVisits(int workers)
  {
    var visits = Traffic();
    using var engine = TestEngine.Create(directory, o => ConfigureTraffic(o, workers));
    TrackTraffic(engine, visits);
    var from = Start.AddDays(7);
    var to = Start.AddDays(14);
    var current = SelectVisits(visits, from, to);
    var converted = current.Where(v => v.Hits.Any(h => h.Event == "purchase")).Select(v => v.Visitor).ToHashSet();
    foreach (var scope in new[] { SegmentScope.Visitors, SegmentScope.Sessions })
      foreach (var exclude in new[] { false, true })
      {
        var segment = scope == SegmentScope.Visitors ? Segment.VisitorsWho("purchase") : Segment.SessionsWith("purchase");
        if (exclude)
          segment = segment.Exclude();
        bool Allowed(Visit visit) => (scope == SegmentScope.Visitors ? converted.Contains(visit.Visitor) :
            visit.Hits.Any(h => h.Event == "purchase")) != exclude;
        var selected = current.Where(Allowed).ToList();
        var query = new AnalyticsQuery
        {
          Range = Range(from, to), Segments = { segment }, Interval = TimeInterval.Day,
          Fill = FillMode.None, GroupBy = { "country" }
        };
        query.Metrics.AddRange(TrafficMetrics);
        var result = engine.Query(query);
        Assert.That(result.Plan.IsComplete, Is.True);
        CheckTrafficRow(result.Totals, selected, from, to, null, "day", null, $"segment {scope}/{exclude}");
        Assert.That(result.Rows.Count, Is.EqualTo(selected.Select(v => (v.Time.Date, v.Country)).Distinct().Count()));
        foreach (var row in result.Rows)
          CheckTrafficRow(row, selected, from, to, row["country"], "day", row.Bucket?.UtcDateTime, "segment row");
      }
  }

  [TestCase(QuerySource.RawEvents, FillMode.None)]
  [TestCase(QuerySource.RawEvents, FillMode.Zero)]
  [TestCase(QuerySource.RawEvents, FillMode.Previous)]
  [TestCase(QuerySource.RawEvents, FillMode.Linear)]
  [TestCase(QuerySource.Rollups, FillMode.None)]
  [TestCase(QuerySource.Rollups, FillMode.Zero)]
  [TestCase(QuerySource.Rollups, FillMode.Previous)]
  [TestCase(QuerySource.Rollups, FillMode.Linear)]
  [TestCase(QuerySource.Auto, FillMode.None)]
  [TestCase(QuerySource.Auto, FillMode.Zero)]
  [TestCase(QuerySource.Auto, FillMode.Previous)]
  [TestCase(QuerySource.Auto, FillMode.Linear)]
  public void NormalSeriesFunctionsUseFilledValuesAndIndependentWindowArithmetic(QuerySource source, FillMode fill)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Rollups.Definitions.Add(new RollupDefinition("machine"));
      o.Rollups.ValueAggregates["x"] = ValueAggregates.FirstAndLast;
    });
    double[][] input = [[120, 135, 125, 140, 155, 150, 170], [10, 30, 5, 20, 42, 3, 12]];
    // Ordinary gauges/counters with one missing reading in the middle of the range.
    for (var machine = 0; machine < input.Length; ++machine)
      for (var day = 0; day < input[machine].Length; ++day)
        if (day != 3)
          engine.Track(new AnalyticsEvent("gauge")
          {
            Timestamp = Utc(Start.AddDays(day).AddHours(12)),
            Dimensions = { ["machine"] = "m" + machine }, Values = { ["x"] = input[machine][day] }
          });
    engine.Flush();
    var last = Metric.Last("x");
    Metric[] metrics = [last, Metric.Cumulative(last), Metric.MovingAverage(last, 3), Metric.Lag(last),
      Metric.Lead(last), Metric.Difference(last), Metric.Increase(last), Metric.Rate(last, TimeSpan.FromDays(1)),
      (last - Metric.Lag(last)) / Metric.Lag(last), Metric.PercentOfTotal(last)];
    var query = new AnalyticsQuery
    {
      Event = "gauge", Range = Range(Start, Start.AddDays(7)), Interval = TimeInterval.Day,
      Source = source, Fill = fill, GroupBy = { "machine" }
    };
    query.Metrics.AddRange(metrics);
    var result = engine.Query(query);
    double totalDifference = 0, totalIncrease = 0;
    foreach (var machine in Enumerable.Range(0, input.Length))
    {
      var expected = input[machine].ToArray();
      expected[3] = fill switch
      {
        FillMode.None or FillMode.Zero => 0,
        FillMode.Previous => expected[2],
        FillMode.Linear => (expected[2] + expected[4]) / 2,
        _ => throw new ArgumentException("Unknown fill mode.", nameof(fill))
      };
      var rows = result.Rows.Where(r => r["machine"] == "m" + machine).OrderBy(r => r.Bucket).ToArray();
      var days = Enumerable.Range(0, 7).Where(d => fill != FillMode.None || d != 3).ToArray();
      Assert.That(rows.Length, Is.EqualTo(days.Length));
      for (var position = 0; position < rows.Length; ++position)
      {
        var day = days[position];
        var previous = position == 0 ? expected[day] : expected[days[position - 1]];
        var difference = expected[day] - previous;
        var increase = difference < 0 ? expected[day] : difference;
        totalDifference += difference;
        totalIncrease += increase;
        double[] values =
        [
          expected[day], days.Take(position + 1).Sum(d => expected[d]),
          (double)expected.Skip(Math.Max(0, day - 2)).Take(Math.Min(3, day + 1)).Select(v => (decimal)v).Average(),
          day == 0 ? 0 : expected[day - 1], day == 6 ? 0 : expected[day + 1], difference, increase,
          expected[day], day == 0 || expected[day - 1] == 0 ? 0 : (expected[day] - expected[day - 1]) / expected[day - 1],
          expected[day] / input.Max(a => a[6])
        ];
        Assert.That(rows[position].Bucket?.UtcDateTime, Is.EqualTo(Start.AddDays(day)));
        for (var m = 0; m < metrics.Length; ++m)
          Assert.That(rows[position][metrics[m]], Is.EqualTo(values[m]).Within(1e-10), $"{fill}/machine{machine}/day{day}/{metrics[m]}");
      }
    }
    Assert.That(result.Totals[Metric.Difference(last)], Is.EqualTo(totalDifference));
    Assert.That(result.Totals[Metric.Increase(last)], Is.EqualTo(totalIncrease));
    // Last's total is the last observation in the whole query, with value breaking a timestamp tie.
    Assert.That(result.Totals[last], Is.EqualTo(170));
  }

  sealed record FunnelHit(string Visitor, int Session, DateTime Time, int Step, string Country);

  // Enumerate every ordered subsequence. This deliberately does not share the engine's state machine.
  static int Furthest(FunnelHit[] hits, TimeSpan? window)
  {
    int Explore(int from, int step, DateTime? first)
    {
      var best = step;
      for (var i = from; i < hits.Length; ++i)
        if (hits[i].Step == step && (!window.HasValue || !first.HasValue || hits[i].Time - first.Value <= window.Value))
          best = Math.Max(best, Explore(i + 1, step + 1, first ?? hits[i].Time));
      return best;
    }
    return Explore(0, 0, null);
  }

  [TestCase(1, false)]
  [TestCase(4, false)]
  [TestCase(1, true)]
  [TestCase(4, true)]
  public void OrdinaryFunnelsMatchExhaustiveOrderedSubsequenceCounts(int parallelism, bool windowed)
  {
    var random = new Random(47017);
    var hits = new List<FunnelHit>();
    for (var visitor = 0; visitor < 60; ++visitor)
      for (var session = 0; session < 3; ++session)
        for (var eventIndex = 0; eventIndex < 8; ++eventIndex)
          hits.Add(new FunnelHit("v" + visitor, session,
              Start.AddHours(8 + session * 4).AddMinutes(visitor % 10 + eventIndex * 2),
              random.Next(5), visitor % 3 == 0 ? "US" : "DE"));
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Ingestion.WorkerCount = parallelism;
      o.Queries.MaxDegreeOfParallelism = parallelism;
    });
    foreach (var h in hits.OrderBy(h => h.Time))
      engine.Track(new AnalyticsEvent(new[] { "landing", "product", "cart", "purchase", "noise" }[h.Step])
      {
        VisitorId = h.Visitor, Timestamp = Utc(h.Time), Dimensions = { ["country"] = h.Country }
      });
    engine.Flush();
    TimeSpan? window = windowed ? TimeSpan.FromHours(5) : null;
    foreach (var country in new[] { null, "DE" })
    {
      var query = new FunnelQuery
      {
        Range = Day(Start), Window = window,
        Steps = { FunnelStep.Goal("landing"), FunnelStep.Goal("product"), FunnelStep.Goal("cart"), FunnelStep.Goal("purchase") }
      };
      if (country != null)
        query.Filters.Add(Filter.Equal("country", country));
      var best = hits.Where(h => country == null || h.Country == country).GroupBy(h => h.Visitor).Select(visitor =>
          windowed ? Furthest(visitor.OrderBy(h => h.Time).ToArray(), window) :
          visitor.GroupBy(h => h.Session).Max(s => Furthest(s.OrderBy(h => h.Time).ToArray(), null))).ToArray();
      var expected = Enumerable.Range(1, 4).Select(step => best.Count(b => b >= step)).ToArray();
      var result = engine.Query(query);
      Assert.That(result.Plan.IsComplete, Is.True);
      for (var step = 0; step < expected.Length; ++step)
      {
        var prior = step == 0 ? expected[0] : expected[step - 1];
        Assert.Multiple(() =>
        {
          Assert.That(result.Steps[step].Visitors, Is.EqualTo(expected[step]));
          Assert.That(result.Steps[step].DropOff, Is.EqualTo(prior - expected[step]));
          Assert.That(result.Steps[step].ConversionRate, Is.EqualTo((double)expected[step] / expected[0]).Within(1e-12));
          Assert.That(result.Steps[step].StepConversionRate, Is.EqualTo((double)expected[step] / prior).Within(1e-12));
        });
      }
    }
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  [TestCase(QuerySource.Auto)]
  public void OrdinaryRatesUseTheElapsedPartOfEachBucket(QuerySource source)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Sessions.Enabled = false;
      o.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
    });
    var timestamps = new List<DateTime>();
    for (var minute = 0; minute < 180; ++minute)
      for (var repeat = 0; repeat <= minute % 3; ++repeat)
      {
        var time = Start.AddHours(9).AddMinutes(minute).AddSeconds(10 + repeat);
        timestamps.Add(time);
        engine.Track(new AnalyticsEvent("request") { Timestamp = Utc(time), Values = { ["cost"] = 2.5 } });
      }
    engine.Flush();
    var from = Start.AddHours(9).AddMinutes(15);
    var to = Start.AddHours(11).AddMinutes(45);
    var requestsPerHour = Metric.Rate(Metric.Events, TimeSpan.FromHours(1));
    var costPerMinute = Metric.Rate(Metric.Sum("cost"), TimeSpan.FromMinutes(1));
    var share = Metric.PercentOfTotal(Metric.Events);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "request", Range = Range(from, to), Source = source, Interval = TimeInterval.Hour,
      Metrics = { Metric.Events, requestsPerHour, costPerMinute, share }
    });
    var total = timestamps.Count(t => t >= from && t < to);
    Assert.That(result.Rows.Count, Is.EqualTo(3));
    Assert.That(result.Plan.IsComplete, Is.True);
    foreach (var row in result.Rows)
    {
      var bucket = row.Bucket.Value.UtcDateTime;
      var seconds = (new[] { to, bucket.AddHours(1) }.Min() - new[] { from, bucket }.Max()).TotalSeconds;
      var count = timestamps.Count(t => t >= from && t < to && Bucket(t, "hour") == bucket);
      Assert.Multiple(() =>
      {
        Assert.That(row[Metric.Events], Is.EqualTo(count));
        Assert.That(row[requestsPerHour], Is.EqualTo(count * 3600.0 / seconds).Within(1e-12));
        Assert.That(row[costPerMinute], Is.EqualTo(count * 2.5 * 60 / seconds).Within(1e-12));
        Assert.That(row[share], Is.EqualTo((double)count / total).Within(1e-12));
      });
    }
    Assert.That(result.Totals[requestsPerHour], Is.EqualTo(total * 3600 / (to - from).TotalSeconds).Within(1e-12));
    Assert.That(result.Totals[costPerMinute], Is.EqualTo(total * 2.5 * 60 / (to - from).TotalSeconds).Within(1e-12));
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  [TestCase(QuerySource.Auto)]
  public void HavingOrderingAndPagingPreserveUnfilteredTotalsAndShares(QuerySource source)
  {
    using var engine = TestEngine.Create(directory, o => o.Rollups.Definitions.Add(new RollupDefinition("category")));
    for (var group = 0; group < 12; ++group)
      for (var index = 0; index <= group; ++index)
        engine.Track(new AnalyticsEvent("purchase")
        {
          VisitorId = "u" + index % 4, Timestamp = Utc(Start.AddHours(group)),
          Dimensions = { ["category"] = "g" + group }, Values = { ["revenue"] = 1.25 * (group + 1) }
        });
    engine.Flush();
    var share = Metric.PercentOfTotal(Metric.Events);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "purchase", Range = Day(Start), Source = source, GroupBy = { "category" },
      Metrics = { Metric.Events, Metric.Visitors, Metric.Sum("revenue"), Metric.Average("revenue"), share },
      Having = { MetricFilter.GreaterThanOrEqual(Metric.Events, 5) },
      OrderBy = { Order.By(Metric.Events) }, Offset = 2, Limit = 3
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Plan.IsComplete, Is.True);
      Assert.That(result.TotalRows, Is.EqualTo(8));
      Assert.That(result.Rows.Select(r => r["category"]), Is.EqualTo(new[] { "g9", "g8", "g7" }));
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(78));
      Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(4));
      Assert.That(result.Totals[Metric.Sum("revenue")], Is.EqualTo(Enumerable.Range(1, 12).Sum(n => n * n * 1.25)));
      Assert.That(result.Totals[Metric.Average("revenue")], Is.EqualTo(Enumerable.Range(1, 12).Sum(n => n * n * 1.25) / 78).Within(1e-10));
    });
    for (var rowIndex = 0; rowIndex < result.Rows.Count; ++rowIndex)
    {
      var count = 10 - rowIndex;
      var row = result.Rows[rowIndex];
      Assert.That(row[Metric.Events], Is.EqualTo(count));
      Assert.That(row[Metric.Sum("revenue")], Is.EqualTo(count * count * 1.25));
      Assert.That(row[Metric.Average("revenue")], Is.EqualTo(count * 1.25));
      Assert.That(row[share], Is.EqualTo(count / 78.0));
    }
  }

  [TestCase("Europe/Berlin")]
  [TestCase("America/New_York")]
  public void OrdinaryLocalDayReportsMatchWallClockDates(string zoneId)
  {
    var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
    using var engine = TestEngine.Create(directory, o => o.Sessions.Enabled = false);
    var input = Enumerable.Range(0, 7 * 24 * 4).Select(i => Start.AddMinutes(i * 15 + 7)).ToArray();
    foreach (var time in input)
      engine.Track(new AnalyticsEvent("request") { Timestamp = Utc(time), Values = { ["cost"] = 1.25 } });
    engine.Flush();
    var from = Start.AddHours(3).AddMinutes(15);
    var to = Start.AddDays(6).AddHours(17).AddMinutes(45);
    var grouped = input.Where(t => t >= from && t < to)
        .GroupBy(t => TimeZoneInfo.ConvertTime(Utc(t), zone).Date).ToDictionary(g => g.Key, g => g.Count());
    foreach (var source in new[] { QuerySource.RawEvents, QuerySource.Auto })
    {
      var query = new AnalyticsQuery
      {
        Event = "request", Range = Range(from, to), TimeZone = zone, Interval = TimeInterval.Day,
        Source = source, Metrics = { Metric.Events, Metric.Sum("cost") }
      };
      var result = engine.Query(query);
      Assert.That(result.Plan.IsComplete, Is.True);
      Assert.That(result.Rows.Count, Is.EqualTo(grouped.Count));
      foreach (var row in result.Rows)
      {
        var count = grouped[row.Bucket.Value.Date];
        Assert.That(row[Metric.Events], Is.EqualTo(count));
        Assert.That(row[Metric.Sum("cost")], Is.EqualTo(count * 1.25));
      }
      Assert.That(result.Totals[Metric.Events], Is.EqualTo(grouped.Values.Sum()));
    }
  }

  [Test]
  public void OrdinaryAudienceSizeHasExactEventCountsAndVisitorEstimatesWithinExpectedError()
  {
    const int visitors = 20000;
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Sessions.Enabled = false;
      o.Rollups.Definitions.Clear();
      o.Rollups.Definitions.Add(new RollupDefinition());
      o.Rollups.Definitions.Add(new RollupDefinition("country"));
    });
    for (var visitor = 0; visitor < visitors; ++visitor)
      for (var repeat = 0; repeat < 2; ++repeat)
        engine.Track(new AnalyticsEvent("pageview")
        {
          VisitorId = "audience-" + visitor, Timestamp = Utc(Start.AddHours(8 + repeat)), Url = "/",
          Dimensions = { ["country"] = visitor % 2 == 0 ? "DE" : "US" }
        });
    engine.Flush();
    foreach (var source in Sources)
    {
      var result = engine.Query(new AnalyticsQuery
      {
        Range = Day(Start), Source = source, GroupBy = { "country" }, Metrics = { Metric.PageViews, Metric.Visitors }
      });
      Assert.That(result.Plan.IsComplete, Is.True);
      Assert.That(result.Rows.Count, Is.EqualTo(2));
      Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(visitors * 2));
      // Default precision 14 has ~0.81% standard error. A fixed sample is checked against
      // three standard errors; this is an empirical sample, not a worst-case HLL guarantee.
      Assert.That(result.Totals[Metric.Visitors], Is.EqualTo(visitors).Within(visitors * 3 * 1.04 / 128));
      TestContext.Out.WriteLine($"{source}: {result.Totals[Metric.Visitors]} estimated / {visitors} actual visitors");
      foreach (var row in result.Rows)
      {
        Assert.That(row[Metric.PageViews], Is.EqualTo(visitors));
        Assert.That(row[Metric.Visitors], Is.EqualTo(visitors / 2).Within(visitors / 2 * 3 * 1.04 / 128));
      }
    }
  }
}
