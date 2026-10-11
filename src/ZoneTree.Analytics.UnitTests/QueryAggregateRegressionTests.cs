using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class QueryAggregateRegressionTests : DirectoryTest
{
  static readonly DateTime Day0 = DateTime.UtcNow.Date.AddDays(-3);

  static RollupValue StoredValue(double value, long ticks, string visitor)
  {
    var accumulator = new RollupAccumulator { Count = 1 };
    accumulator.AddVisitor(Text.StableHash(visitor), 14);
    foreach (var name in new[] { "requested", "unused" })
    {
      accumulator.AddValue(name, value);
      accumulator.AddValueDetail(name, value, ticks, ValueAggregates.All, QuantileSketch.BucketOf(value));
    }
    foreach (var dimension in new[] { "tenant", "unused" })
      accumulator.AddDistinct(dimension, Text.StableHash(visitor), 14);
    return accumulator.ToValue();
  }

  [TestCase(ValueAggregates.None, false)]
  [TestCase(ValueAggregates.FirstAndLast, false)]
  [TestCase(ValueAggregates.Percentiles, true)]
  [TestCase(ValueAggregates.All, true)]
  public void SelectiveMergesPreserveRequestedAggregatesAndStoredValues(ValueAggregates details, bool visitors)
  {
    var first = StoredValue(10, 100, "a");
    var second = StoredValue(30, 200, "b");
    var serializer = new RollupValueSerializer();
    var firstBytes = serializer.Serialize(first).ToArray();
    var secondBytes = serializer.Serialize(second).ToArray();
    var requirements = new RollupReadRequirements(visitors,
        details == ValueAggregates.None ? null : new() { ["requested"] = details }, ["tenant"]);
    var group = new RollupAccumulator();
    group.MergeForQuery(first, requirements);
    group.MergeForQuery(second, requirements);
    var totals = new RollupAccumulator();
    totals.Merge(group);
    // Merging more rows into either side must not mutate shared details or sketches.
    totals.MergeForQuery(first, requirements);
    group.MergeForQuery(second, requirements);
    Assert.Multiple(() =>
    {
      Assert.That(serializer.Serialize(first).ToArray(), Is.EqualTo(firstBytes));
      Assert.That(serializer.Serialize(second).ToArray(), Is.EqualTo(secondBytes));
      Assert.That(group.Count, Is.EqualTo(3));
      Assert.That(totals.Count, Is.EqualTo(3));
      Assert.That(group.Visitors?.Estimate(), Is.EqualTo(visitors ? 2.0 : (double?)null));
      Assert.That(group.DistinctOf("tenant").Estimate(), Is.EqualTo(2));
      Assert.That(group.DistinctOf("unused"), Is.Null);
      Assert.That(group.DetailOf("unused"), Is.Null);
      Assert.That(group.TryGetValueStats("requested", out var stats) ? stats.Sum : 0, Is.EqualTo(70));
      Assert.That(totals.TryGetValueStats("requested", out var totalStats) ? totalStats.Sum : 0, Is.EqualTo(50));
      if (details == ValueAggregates.None)
        Assert.That(group.DetailOf("requested"), Is.Null);
      else
      {
        var detail = group.DetailOf("requested");
        Assert.That(detail.HasFirstLast, Is.EqualTo((details & ValueAggregates.FirstAndLast) != 0));
        if (detail.HasFirstLast)
          Assert.That((detail.First, detail.Last), Is.EqualTo((10.0, 30.0)));
        Assert.That(detail.Quantiles?.Count, Is.EqualTo((details & ValueAggregates.Percentiles) != 0 ? 3L : (long?)null));
        Assert.That(totals.DetailOf("requested").Quantiles?.Count,
            Is.EqualTo((details & ValueAggregates.Percentiles) != 0 ? 3L : (long?)null));
      }
    });
  }

  [Test]
  public void SelectiveMergesPreserveDeltaCancellationAndVisitorOnlyRows()
  {
    var positive = new RollupAccumulator();
    positive.AddValueDelta(StandardValues.Exits, 1);
    var negative = new RollupAccumulator();
    negative.AddValueDelta(StandardValues.Exits, -1);
    var group = new RollupAccumulator();
    group.MergeForQuery(positive.ToValue(), default);
    Assert.That(group.HasData, Is.True);
    group.MergeForQuery(negative.ToValue(), default);
    Assert.That(group.HasData, Is.False);
    var visitorOnly = new RollupAccumulator();
    visitorOnly.AddVisitor(Text.StableHash("a"), 14);
    group.MergeForQuery(visitorOnly.ToValue(), default);
    Assert.That(group.Visitors, Is.Null);
    Assert.That(group.HasData, Is.True);
    var total = new RollupAccumulator();
    total.Merge(group);
    Assert.That(total.HasData, Is.True);
  }

  [TestCase(QuerySource.RawEvents)]
  [TestCase(QuerySource.Rollups)]
  [TestCase(QuerySource.Auto)]
  public void RequirementsIncludeFormulaHavingAndConditionalSources(QuerySource source)
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.Rollups.ValueAggregates["amount"] = ValueAggregates.All;
      o.Rollups.ValueAggregates["unused"] = ValueAggregates.All;
      o.Rollups.DistinctDimensions.Add("tenant");
      o.Rollups.DistinctDimensions.Add("unused");
      o.Rollups.Definitions.Add(new RollupDefinition("room"));
    });
    foreach (var (room, hour, visitor, amount) in new[]
    {
      ("a", 0, "one", 10), ("a", 1, "two", 30), ("a", 2, "one", 20), ("b", 0, "three", 50)
    })
    {
      engine.Track(new AnalyticsEvent("reading")
      {
        VisitorId = visitor, Timestamp = Utc(Day0.AddHours(hour)),
        Dimensions = { ["room"] = room, ["tenant"] = visitor, ["unused"] = visitor },
        Values = { ["amount"] = amount, ["unused"] = amount * 10 }
      });
      if (visitor == "one")
        engine.Track(new AnalyticsEvent("signup")
        {
          VisitorId = visitor, Timestamp = Utc(Day0.AddHours(hour)), Dimensions = { ["room"] = room }
        });
    }
    engine.Flush();
    var inA = Filter.Equal("room", "a");
    var average = Metric.Sum("amount") / Metric.Events;
    var first = Metric.First("amount").Where(inA);
    var rate = Metric.ConversionRate("signup").Where(inA);
    var result = engine.Query(new AnalyticsQuery
    {
      Event = "reading", Range = Day(Day0), Source = source, GroupBy = { "room" },
      Metrics = { average, first, rate, Metric.Events },
      Having =
      {
        MetricFilter.GreaterThanOrEqual(Metric.Visitors.Where(inA), 2),
        MetricFilter.GreaterThanOrEqual(Metric.Distinct("tenant"), 2),
        MetricFilter.GreaterThan(Metric.Percentile("amount", 50), 0)
      }
    });
    var row = result.Rows.Single();
    Assert.Multiple(() =>
    {
      Assert.That(row["room"], Is.EqualTo("a"));
      Assert.That(row[average], Is.EqualTo(20));
      Assert.That(row[first], Is.EqualTo(10));
      Assert.That(row[rate], Is.EqualTo(0.5));
      Assert.That(row[Metric.Events], Is.EqualTo(3));
      Assert.That(result.Totals[average], Is.EqualTo(27.5));
      Assert.That(result.Totals[first], Is.EqualTo(10));
      Assert.That(result.Totals[rate], Is.EqualTo(0.5));
    });
    // A later query must still see opt-in data skipped by the earlier query.
    var later = engine.Query(new AnalyticsQuery
    {
      Event = "reading", Range = Day(Day0), Source = source,
      Metrics = { Metric.Last("unused"), Metric.Percentile("unused", 100), Metric.Distinct("unused"), Metric.Visitors }
    });
    Assert.Multiple(() =>
    {
      Assert.That(later.Totals[Metric.Last("unused")], Is.EqualTo(200));
      Assert.That(later.Totals[Metric.Percentile("unused", 100)], Is.EqualTo(500));
      Assert.That(later.Totals[Metric.Distinct("unused")], Is.EqualTo(3));
      Assert.That(later.Totals[Metric.Visitors], Is.EqualTo(3));
    });
  }
}
