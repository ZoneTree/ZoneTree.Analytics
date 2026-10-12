using System.Globalization;

namespace ZoneTree.Analytics.Internal;

/// <summary>Time dimensions are computed at the event or storage bucket, before output bucketing.</summary>
static class TimeDimension
{
  static readonly string[] Hours = Enumerable.Range(0, 24).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray();
  static readonly string[] Days = Enumerable.Range(1, 7).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray();

  public static bool IsDerived(string name) => name is StandardDimensions.HourOfDay or StandardDimensions.DayOfWeek;

  public static string Value(string name, long ticks, ReportingCalendar calendar, bool local = false)
  {
    var date = local ? new DateTime(ticks) :
        TimeZoneInfo.ConvertTimeFromUtc(new DateTime(ticks, DateTimeKind.Utc), calendar.TimeZone);
    return name == StandardDimensions.HourOfDay ? Hours[date.Hour] : Days[((int)date.DayOfWeek + 6) % 7];
  }

  /// <summary>A UTC bucket must not span a change of local hour or date.</summary>
  public static bool CanRead(Granularity granularity, ReportingCalendar calendar, string[] dimensions, long from, long to)
  {
    if (dimensions.Length == 0)
      return true;
    if (granularity == Granularity.Day)
      return !dimensions.Contains(StandardDimensions.HourOfDay);
    var width = granularity == Granularity.Hour ? TimeSpan.TicksPerHour : TimeSpan.TicksPerMinute;
    var zone = calendar.TimeZone;
    if (zone.BaseUtcOffset.Ticks % width != 0)
      return false;
    var start = new DateTime(from, DateTimeKind.Utc).Date;
    var end = new DateTime(to - 1, DateTimeKind.Utc).Date;
    // Adjustment-rule dates are local dates, which can be adjacent to the UTC scan dates.
    if (start > DateTime.MinValue.Date)
      start = start.AddDays(-1);
    if (end < DateTime.MaxValue.Date)
      end = end.AddDays(1);
    foreach (var rule in zone.GetAdjustmentRules())
    {
      if (rule.DateEnd < start || rule.DateStart > end)
        continue;
      if (rule.BaseUtcOffsetDelta.Ticks % width != 0 || rule.DaylightDelta.Ticks % width != 0)
        return false;
      if (rule.DaylightDelta == TimeSpan.Zero)
        continue;
      var begins = rule.DaylightTransitionStart.TimeOfDay.TimeOfDay.Ticks;
      var ends = rule.DaylightTransitionEnd.TimeOfDay.TimeOfDay.Ticks;
      if (begins % width != 0)
        return false;
      if (ends % width != 0)
      {
        // Some system zones describe the last millisecond before a transition. Custom
        // rules instead describe the transition itself. Check the runtime's offsets;
        // accepting both representations just from the time would misclassify custom zones.
        if ((ends + TimeSpan.TicksPerMillisecond) % width != 0 ||
            !EndsAtBoundary(zone, rule, start.Year, end.Year))
          return false;
      }
    }
    return true;
  }

  static bool EndsAtBoundary(TimeZoneInfo zone, TimeZoneInfo.AdjustmentRule rule, int firstYear, int lastYear)
  {
    var daylight = zone.BaseUtcOffset + rule.BaseUtcOffsetDelta + rule.DaylightDelta;
    var standard = zone.BaseUtcOffset + rule.BaseUtcOffsetDelta;
    for (var year = firstYear; year <= lastYear; ++year)
    {
      var transition = rule.DaylightTransitionEnd;
      var date = new DateTime(year, transition.Month, 1);
      if (transition.IsFixedDateRule)
        date = date.AddDays(Math.Min(transition.Day, DateTime.DaysInMonth(year, transition.Month)) - 1);
      else
      {
        var day = ((int)transition.DayOfWeek - (int)date.DayOfWeek + 7) % 7 + (transition.Week - 1) * 7;
        if (day >= DateTime.DaysInMonth(year, transition.Month))
          day -= 7;
        date = date.AddDays(day);
      }
      if (date < rule.DateStart || date > rule.DateEnd)
        continue;
      var ticks = date.Ticks + transition.TimeOfDay.TimeOfDay.Ticks - daylight.Ticks;
      var boundary = ticks + TimeSpan.TicksPerMillisecond;
      if (ticks < DateTime.MinValue.Ticks || boundary > DateTime.MaxValue.Ticks)
        return false;
      // The offset must stay unchanged through the last tick of the bucket, and change
      // exactly at the following boundary. This is safe for both system and custom zones.
      if (zone.GetUtcOffset(new DateTime(boundary - 1, DateTimeKind.Utc)) != daylight ||
          zone.GetUtcOffset(new DateTime(boundary, DateTimeKind.Utc)) != standard)
        return false;
    }
    return true;
  }
}
