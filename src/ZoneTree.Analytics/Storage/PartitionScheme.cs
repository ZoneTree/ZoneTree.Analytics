using System.Globalization;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Maps bucket ticks to time partitions. Each partition is one ZoneTree directory
/// named after its period: "2026", "2026-10" or "2026-10-08".
/// </summary>
sealed class PartitionScheme
{
  readonly string format;

  public PartitionScheme(PartitionPeriod period)
  {
    Period = period;
    format = period switch
    {
      PartitionPeriod.Day => "yyyy-MM-dd",
      PartitionPeriod.Month => "yyyy-MM",
      PartitionPeriod.Year => "yyyy",
      _ => throw new ArgumentOutOfRangeException(nameof(period))
    };
  }

  public PartitionPeriod Period { get; }

  public long StartOf(long ticks)
  {
    var date = new DateTime(ticks);
    return Period switch
    {
      PartitionPeriod.Day => date.Date.Ticks,
      PartitionPeriod.Month => new DateTime(date.Year, date.Month, 1).Ticks,
      _ => new DateTime(date.Year, 1, 1).Ticks
    };
  }

  public long NextStart(long start)
  {
    var date = new DateTime(start);
    return Period switch
    {
      PartitionPeriod.Day => date.AddDays(1).Ticks,
      PartitionPeriod.Month => date.AddMonths(1).Ticks,
      _ => date.AddYears(1).Ticks
    };
  }

  public string NameOf(long ticks) => new DateTime(ticks).ToString(format, CultureInfo.InvariantCulture);

  public bool TryParse(string name, out long start)
  {
    if (DateTime.TryParseExact(
        name, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
    {
      start = date.Ticks;
      return true;
    }
    start = 0;
    return false;
  }

  /// <summary>
  /// Names of partitions that overlap [from, to).
  /// </summary>
  public IEnumerable<string> NamesInRange(long fromTicks, long toTicks)
  {
    if (fromTicks >= toTicks)
      yield break;
    var start = StartOf(fromTicks);
    while (start < toTicks)
    {
      yield return NameOf(start);
      start = NextStart(start);
    }
  }
}
