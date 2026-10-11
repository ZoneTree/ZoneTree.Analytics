using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Query;

namespace ZoneTree.Analytics.UnitTests;

public sealed class QueryRangePlannerTests
{
  static readonly ReportingCalendar UtcCalendar = new(TimeZoneInfo.Utc, DayOfWeek.Monday);

  static readonly long Start = new DateTime(2026, 1, 1).Ticks;

  static void AssertContinuousCoverage(ReadPlan plan, long from, long to)
  {
    var segments = plan.Segments.OrderBy(x => x.From).ToArray();
    Assert.Multiple(() =>
    {
      Assert.That(segments[0].From, Is.EqualTo(from));
      Assert.That(segments[^1].To, Is.EqualTo(to));
      Assert.That(segments.All(x => x.From < x.To), Is.True);
      for (var i = 1; i < segments.Length; ++i)
        Assert.That(segments[i].From, Is.EqualTo(segments[i - 1].To), "no gaps or overlapping buckets");
    });
  }

  [Test]
  public void MixedTiersCoverTheExactRangeOnce()
  {
    var from = Start + TimeSpan.FromSeconds(20).Ticks;
    var to = Start + TimeSpan.FromHours(2).Ticks + TimeSpan.FromSeconds(160).Ticks;
    var plan = QueryRangePlanner.Build(UtcCalendar, from, to,
        [new(Granularity.Day, 0), new(Granularity.Hour, 0), new(Granularity.Minute, 0)], true, default);
    AssertContinuousCoverage(plan, from, to);
    Assert.Multiple(() =>
    {
      Assert.That(plan.Segments.Select(x => x.Source).Distinct(), Is.EquivalentTo(
          new[] { PlanSource.HourRollups, PlanSource.MinuteRollups, PlanSource.RawEvents }));
      Assert.That(plan.WholeBucketSources, Is.EqualTo(Granularity.None));
      Assert.That(plan.HasMissingRanges, Is.False);
    });
  }

  [Test]
  public void WholeBucketFallbackWidensOnlyTheOuterBounds()
  {
    var plan = QueryRangePlanner.Build(UtcCalendar,
        Start + TimeSpan.FromMinutes(15).Ticks, Start + TimeSpan.FromMinutes(165).Ticks,
        [new(Granularity.Day, 0), new(Granularity.Hour, 0)], false, default);
    AssertContinuousCoverage(plan, Start, Start + TimeSpan.FromHours(3).Ticks);
    Assert.Multiple(() =>
    {
      Assert.That(plan.Segments.Select(x => x.Source), Is.All.EqualTo(PlanSource.HourRollups));
      Assert.That(plan.WholeBucketSources, Is.EqualTo(Granularity.Hour));
      Assert.That(plan.HasMissingRanges, Is.False);
    });
  }

  [Test]
  public void DailyCoverageUsesTheActualLengthOfADaylightSavingDay()
  {
    var calendar = new ReportingCalendar(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), DayOfWeek.Monday);
    var from = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5)).UtcTicks;
    var to = new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.FromHours(-4)).UtcTicks;
    var plan = QueryRangePlanner.Build(calendar, from, to, [new(Granularity.Day, 0)], false, default);
    Assert.Multiple(() =>
    {
      Assert.That(to - from, Is.EqualTo(TimeSpan.FromHours(23).Ticks));
      Assert.That(plan.Segments, Is.EqualTo(new[] { new ReadSegment(PlanSource.DayRollups, from, to) }));
      Assert.That(plan.WholeBucketSources, Is.EqualTo(Granularity.None));
    });
  }

  [Test]
  public void ANewTierCannotClaimOlderCompleteCoverage()
  {
    var to = Start + TimeSpan.TicksPerDay;
    var plan = QueryRangePlanner.Build(UtcCalendar, Start, to,
        [new(Granularity.Day, Start + TimeSpan.FromHours(12).Ticks), new(Granularity.Hour, 0)], false, default);
    AssertContinuousCoverage(plan, Start, to);
    Assert.That(plan.Segments.Select(x => x.Source), Is.All.EqualTo(PlanSource.HourRollups));
    Assert.That(plan.WholeBucketSources, Is.EqualTo(Granularity.None));
  }

  [Test]
  public void FallbackRecordsEveryTierUsedForPartialBuckets()
  {
    var plan = QueryRangePlanner.Build(UtcCalendar,
        Start + TimeSpan.FromSeconds(20).Ticks, Start + TimeSpan.FromHours(2).Ticks + TimeSpan.FromSeconds(20).Ticks,
        [new(Granularity.Hour, 0), new(Granularity.Minute, 0)], false, default);
    AssertContinuousCoverage(plan, Start, Start + TimeSpan.FromHours(3).Ticks);
    Assert.That(plan.WholeBucketSources, Is.EqualTo(Granularity.Minute | Granularity.Hour));
  }
}
