using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Query;

/// <summary>A storage range to read, expressed in UTC.</summary>
readonly record struct ReadSegment(PlanSource Source, long From, long To)
{
  public Granularity Granularity => Source switch
  {
    PlanSource.DayRollups => Granularity.Day,
    PlanSource.HourRollups => Granularity.Hour,
    PlanSource.MinuteRollups => Granularity.Minute,
    _ => throw new InvalidOperationException("Raw events have no rollup granularity.")
  };

  public (long From, long To) Buckets(ReportingCalendar calendar)
  {
    if (Source != PlanSource.DayRollups)
      return (From, To);
    var end = calendar.LocalDay(To);
    if (calendar.UtcStartOfLocalDay(end) < To)
      end += TimeSpan.TicksPerDay;
    return (calendar.LocalDay(From), end);
  }
}

/// <summary>A configured rollup tier and the earliest time it covers.</summary>
readonly record struct RollupCoverage(Granularity Granularity, long AvailableFrom)
{
  public PlanSource Source => Granularity switch
  {
    Granularity.Day => PlanSource.DayRollups,
    Granularity.Hour => PlanSource.HourRollups,
    Granularity.Minute => PlanSource.MinuteRollups,
    _ => throw new InvalidOperationException("A coverage entry must name one rollup tier.")
  };

  public long Floor(ReportingCalendar calendar, long ticks) => Granularity == Granularity.Day
      ? calendar.FloorToDayBoundary(ticks)
      : ReportingCalendar.FloorTicks(ticks, UnitTicks);

  public long Ceil(ReportingCalendar calendar, long ticks) => Granularity == Granularity.Day
      ? calendar.CeilToDayBoundary(ticks)
      : ReportingCalendar.CeilTicks(ticks, UnitTicks);

  long UnitTicks => Granularity == Granularity.Hour ? TimeSpan.TicksPerHour : TimeSpan.TicksPerMinute;
}

sealed class ReadPlan
{
  public List<ReadSegment> Segments { get; } = [];

  public Granularity WholeBucketSources { get; internal set; }

  public bool HasMissingRanges { get; internal set; }
}

/// <summary>
/// Covers a range with whole rollup buckets, then finer tiers or raw events at its edges.
/// Only the outer query bounds may be widened when exact data is unavailable.
/// </summary>
static class QueryRangePlanner
{
  readonly record struct RemainingRange(long From, long To, int Tier, bool WidenStart, bool WidenEnd);

  /// <summary>Plans a range using eligible tiers ordered from coarsest to finest.</summary>
  public static ReadPlan Build(
      ReportingCalendar calendar, long from, long to, IReadOnlyList<RollupCoverage> tiers,
      bool readRaw, CancellationToken cancellationToken, TimeInterval interval = default)
  {
    var plan = new ReadPlan();
    var remaining = new Stack<RemainingRange>();
    remaining.Push(new(from, to, 0, true, true));
    while (remaining.TryPop(out var range))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (range.From >= range.To)
        continue;
      if (range.Tier == tiers.Count)
      {
        if (readRaw)
          plan.Segments.Add(new(PlanSource.RawEvents, range.From, range.To));
        else
          AddWholeBuckets(plan, calendar, tiers, range);
        continue;
      }

      var tier = tiers[range.Tier];
      var start = tier.Ceil(calendar, range.From);
      if (start < tier.AvailableFrom)
        start = tier.Ceil(calendar, tier.AvailableFrom);
      var end = tier.Floor(calendar, range.To);
      if (start >= end)
      {
        remaining.Push(range with { Tier = range.Tier + 1 });
        continue;
      }
      // Split only the tier actually chosen, so aligned daily rollups remain a single
      // contiguous read even when the finer hourly tier has incompatible boundaries.
      var boundary = CrossingBoundary(calendar, tier, start, end, interval, cancellationToken);
      if (boundary != 0)
      {
        remaining.Push(new(boundary, range.To, range.Tier, false, range.WidenEnd));
        remaining.Push(new(range.From, boundary, range.Tier, range.WidenStart, false));
        continue;
      }
      plan.Segments.Add(new(tier.Source, start, end));
      // Push the right edge first so the left edge is visited first. Neither may overlap
      // the middle segment, even if it needs whole-bucket fallback.
      remaining.Push(new(end, range.To, range.Tier + 1, false, range.WidenEnd));
      remaining.Push(new(range.From, start, range.Tier + 1, range.WidenStart, false));
    }
    return plan;
  }

  static long CrossingBoundary(
      ReportingCalendar calendar, RollupCoverage tier, long from, long to,
      TimeInterval interval, CancellationToken cancellationToken)
  {
    if (interval == TimeInterval.None || interval.IsFixedWidth || tier.Granularity == Granularity.Day)
      return 0;
    foreach (var bucket in calendar.EnumerateBuckets(from, to, interval))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var boundary = calendar.UtcStartOfBucket(bucket, interval);
      if (boundary > from && tier.Floor(calendar, boundary) != boundary)
        return boundary;
    }
    return 0;
  }

  static void AddWholeBuckets(
      ReadPlan plan, ReportingCalendar calendar, IReadOnlyList<RollupCoverage> tiers, RemainingRange range)
  {
    foreach (var tier in tiers)
    {
      if (tier.Floor(calendar, range.From) >= tier.AvailableFrom &&
          TryAddWholeBuckets(plan, calendar, tier, range))
        return;
    }
    // Preserve rollup-only queries of older, incomplete coverage. The finest tier may
    // still hold some rows; the warning makes their partial coverage explicit.
    if (tiers.Count > 0)
    {
      var finest = tiers[^1];
      plan.WholeBucketSources |= finest.Granularity;
      if (TryAddWholeBuckets(plan, calendar, finest, range))
        return;
    }
    plan.HasMissingRanges = true;
  }

  static bool TryAddWholeBuckets(
      ReadPlan plan, ReportingCalendar calendar, RollupCoverage tier, RemainingRange range)
  {
    var start = range.WidenStart ? tier.Floor(calendar, range.From) : tier.Ceil(calendar, range.From);
    var end = range.WidenEnd ? tier.Ceil(calendar, range.To) : tier.Floor(calendar, range.To);
    if (start >= end)
      return false;
    plan.Segments.Add(new(tier.Source, start, end));
    plan.WholeBucketSources |= tier.Granularity;
    return true;
  }
}
