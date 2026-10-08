using System.Globalization;

namespace ZoneTree.Analytics.Storage;

enum PartitionSize
{
  Day,
  Month,
  Year,
}

/// <summary>
/// Maps bucket ticks to time partitions. Each partition is one ZoneTree directory
/// named after its period: "2026", "2026-10" or "2026-10-08".
/// </summary>
sealed class PartitionScheme
{
  public PartitionScheme(PartitionSize size)
  {
    Size = size;
  }

  public PartitionSize Size { get; }

  public long StartOf(long ticks)
  {
    var date = new DateTime(ticks);
    return Size switch
    {
      PartitionSize.Day => date.Date.Ticks,
      PartitionSize.Month => new DateTime(date.Year, date.Month, 1).Ticks,
      _ => new DateTime(date.Year, 1, 1).Ticks
    };
  }

  public long NextStart(long start)
  {
    var date = new DateTime(start);
    return Size switch
    {
      PartitionSize.Day => date.AddDays(1).Ticks,
      PartitionSize.Month => date.AddMonths(1).Ticks,
      _ => date.AddYears(1).Ticks
    };
  }

  public string NameOf(long ticks)
  {
    var date = new DateTime(ticks);
    return Size switch
    {
      PartitionSize.Day => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      PartitionSize.Month => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
      _ => date.ToString("yyyy", CultureInfo.InvariantCulture)
    };
  }

  public bool TryParse(string name, out long start)
  {
    var format = Size switch
    {
      PartitionSize.Day => "yyyy-MM-dd",
      PartitionSize.Month => "yyyy-MM",
      _ => "yyyy"
    };
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
