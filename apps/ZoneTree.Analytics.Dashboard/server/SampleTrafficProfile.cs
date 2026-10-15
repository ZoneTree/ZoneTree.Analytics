
// An hourly probability curve keeps generation bounded even for millions of
// events. Sampling its cumulative weights produces coherent traffic patterns.
sealed class SampleTrafficProfile
{
  readonly List<Slice> slices = [];
  readonly double totalWeight;

  sealed record Slice(DateTimeOffset Start, long Ticks, double Weight, double Cumulative, double Campaign);
  public readonly record struct Moment(DateTimeOffset Timestamp, double Campaign);

  public SampleTrafficProfile(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo timeZone)
  {
    var days = (to - from).TotalDays;
    var cumulative = 0.0;
    for (var start = from; start < to;)
    {
      var end = start.AddHours(1);
      if (end > to) end = to;
      var middle = start.AddTicks((end - start).Ticks / 2);
      var local = TimeZoneInfo.ConvertTime(middle, timeZone);
      var position = (middle - from).TotalDays / days;
      var hour = local.TimeOfDay.TotalHours;
      var daily = 0.07 + Bell(hour, 10.5, 2.5) + 0.85 * Bell(hour, 15.5, 2.4)
          + 0.3 * Bell(hour, 20, 1.8);
      var weekday = local.DayOfWeek switch
      {
        DayOfWeek.Saturday => 0.54,
        DayOfWeek.Sunday => 0.45,
        DayOfWeek.Monday => 1.1,
        DayOfWeek.Friday => 0.87,
        _ => 1.0,
      };
      // Stable variation per calendar day; no independent noise for every event.
      var day = DateOnly.FromDateTime(local.DateTime).DayNumber;
      var variation = 0.75 + new Random(day).NextDouble() * 0.5;
      var campaign = 3.8 * Bell(position, 0.34, Math.Max(0.025, 0.4 / days))
          + 2.3 * Bell(position, 0.79, Math.Max(0.04, 0.6 / days));
      var dip = 1 - 0.75 * Bell(position, 0.6, Math.Max(0.018, 0.25 / days));
      var weight = (end - start).TotalHours * daily * weekday * variation
          * (0.6 + 1.3 * position) * (1 + campaign) * dip;
      cumulative += weight;
      slices.Add(new(start, (end - start).Ticks, weight, cumulative, campaign));
      start = end;
    }
    totalWeight = cumulative;
  }

  static double Bell(double value, double center, double width) =>
      Math.Exp(-0.5 * Math.Pow((value - center) / width, 2));

  public Moment At(double fraction)
  {
    var target = Math.Clamp(fraction, 0, 1) * totalWeight;
    var low = 0;
    var high = slices.Count - 1;
    while (low < high)
    {
      var middle = (low + high) / 2;
      if (slices[middle].Cumulative < target) low = middle + 1;
      else high = middle;
    }
    var slice = slices[low];
    var progress = (target - (slice.Cumulative - slice.Weight)) / slice.Weight;
    var offset = Math.Clamp((long)(progress * slice.Ticks), 0, slice.Ticks - 1);
    return new(slice.Start.AddTicks(offset), slice.Campaign);
  }
}
