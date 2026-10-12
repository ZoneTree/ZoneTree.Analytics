using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Internal;

/// <summary>A row's dimension values, without allocating a dictionary or delegate per event.</summary>
readonly struct FilterValues
{
  readonly RawEvent rawEvent;
  readonly IReadOnlyDictionary<string, string> dictionary;
  readonly IReadOnlyList<string> names;
  readonly string[] parts;
  readonly ReportingCalendar calendar;
  readonly long ticks;
  readonly bool local;

  public FilterValues(IReadOnlyDictionary<string, string> dictionary, long ticks = 0, ReportingCalendar calendar = null)
  {
    this.dictionary = dictionary;
    this.ticks = ticks;
    this.calendar = calendar;
  }

  public FilterValues(RawEvent rawEvent, ReportingCalendar calendar)
  {
    this.rawEvent = rawEvent;
    ticks = rawEvent.Ticks;
    this.calendar = calendar;
  }

  public FilterValues(IReadOnlyList<string> names, string[] parts, long ticks, ReportingCalendar calendar, bool local)
  {
    this.names = names;
    this.parts = parts;
    this.ticks = ticks;
    this.calendar = calendar;
    this.local = local;
  }

  public string Get(string name)
  {
    if (calendar != null && TimeDimension.IsDerived(name))
      return TimeDimension.Value(name, ticks, calendar, local);
    if (rawEvent != null)
    {
      rawEvent.TryGetDimension(name, out var value);
      return value;
    }
    if (dictionary != null)
      return dictionary.GetValueOrDefault(name);
    for (var i = 0; i < names.Count; ++i)
    {
      if (names[i] == name)
        return KeyCodec.Public(parts[i]);
    }
    return null;
  }
}
