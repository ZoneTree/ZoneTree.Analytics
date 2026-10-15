using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ZoneTree.Analytics.Internal;

/// <summary>Time dimensions are computed at the event or storage bucket, before output bucketing.</summary>
static class TimeDimension
{
  // Two digits, so hours sort in time order like every other dimension value: ordinally.
  static readonly string[] Hours = Enumerable.Range(0, 24).Select(x => x.ToString("00", CultureInfo.InvariantCulture)).ToArray();
  static readonly string[] Days = Enumerable.Range(1, 7).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray();

  public static bool IsDerived(string name) => name is StandardDimensions.HourOfDay or StandardDimensions.DayOfWeek;

  public static string Value(string name, long ticks, ReportingCalendar calendar, bool local = false)
  {
    var date = local ? new DateTime(ticks) :
        TimeZoneInfo.ConvertTimeFromUtc(new DateTime(ticks, DateTimeKind.Utc), calendar.TimeZone);
    return name == StandardDimensions.HourOfDay ? Hours[date.Hour] : Days[((int)date.DayOfWeek + 6) % 7];
  }

  /// <summary>Per zone: whether the offsets of a (UTC year, bucket width) are bucket aligned.</summary>
  static readonly ConditionalWeakTable<TimeZoneInfo, ConcurrentDictionary<(int Year, long Width), bool>> AlignedYears = new();

  /// <summary>
  /// A UTC bucket must not span a change of local hour or date: every UTC offset in the
  /// range, and every instant where the offset changes, must be a multiple of the bucket width.
  /// </summary>
  public static bool CanRead(Granularity granularity, ReportingCalendar calendar, string[] dimensions, long from, long to)
  {
    if (dimensions.Length == 0)
      return true;
    if (granularity == Granularity.Day)
      return !dimensions.Contains(StandardDimensions.HourOfDay);
    var width = granularity == Granularity.Hour ? TimeSpan.TicksPerHour : TimeSpan.TicksPerMinute;
    var zone = calendar.TimeZone;
    var years = AlignedYears.GetValue(zone, static _ => new());
    var last = new DateTime(Math.Max(from, to), DateTimeKind.Utc).Year;
    for (var year = new DateTime(from, DateTimeKind.Utc).Year; year <= last; ++year)
    {
      if (!years.GetOrAdd((year, width), static (key, zone) => IsAligned(zone, key.Year, key.Width), zone))
        return false;
    }
    return true;
  }

  /// <summary>
  /// Reads the offsets from the runtime, which also converts raw event times, rather than
  /// from adjustment rules: Windows, Unix (TZif data) and custom zones represent the same
  /// transitions with different rule fields. Offsets are sampled daily, and each change is
  /// located to the tick; time zones do not change and restore an offset within one day.
  /// </summary>
  static bool IsAligned(TimeZoneInfo zone, int year, long width)
  {
    // Start a day early, so a change at the start of the year is found too.
    var at = year == 1 ? DateTime.MinValue.Ticks : new DateTime(year, 1, 1).Ticks - TimeSpan.TicksPerDay;
    var end = year == DateTime.MaxValue.Year ? DateTime.MaxValue.Ticks : new DateTime(year + 1, 1, 1).Ticks;
    var offset = Offset(zone, at);
    if (offset % width != 0)
      return false;
    while (at < end)
    {
      var next = Math.Min(at + TimeSpan.TicksPerDay, end);
      var nextOffset = Offset(zone, next);
      while (offset != nextOffset)
      {
        // Find the tick where the offset changes: low keeps the old offset, high does not.
        var low = at;
        var high = next;
        while (high - low > 1)
        {
          var middle = low + (high - low) / 2;
          if (Offset(zone, middle) == offset)
            low = middle;
          else
            high = middle;
        }
        offset = Offset(zone, high);
        if (high % width != 0 || offset % width != 0)
          return false;
        at = high;
      }
      at = next;
    }
    return true;
  }

  static long Offset(TimeZoneInfo zone, long ticks) =>
      zone.GetUtcOffset(new DateTime(ticks, DateTimeKind.Utc)).Ticks;
}
