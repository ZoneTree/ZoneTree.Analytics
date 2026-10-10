using System.Text;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics;

/// <summary>
/// Whether a segment selects visitors or sessions.
/// </summary>
public enum SegmentScope
{
  /// <summary>Visitors with at least one matching event in the range.</summary>
  Visitors,

  /// <summary>Sessions with at least one matching event.</summary>
  Sessions,
}

/// <summary>
/// A behavioral segment, for example "visitors who signed up" or "sessions that viewed /pricing".
/// </summary>
/// <remarks>
/// Segments restrict a query to the events of matching visitors or sessions. They require raw
/// events, so they are available within raw event retention.
/// </remarks>
public sealed class Segment
{
  readonly Filter[] filters;

  Segment(SegmentScope scope, string eventName, Filter[] filters, bool isExcluded)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
    Scope = scope;
    Event = eventName;
    this.filters = filters == null ? [] : [.. filters];
    IsExcluded = isExcluded;
  }

  /// <summary>Visitors or sessions.</summary>
  public SegmentScope Scope { get; }

  /// <summary>The event a visitor or session must have.</summary>
  public string Event { get; }

  /// <summary>Conditions on that event. All must match.</summary>
  public IReadOnlyList<Filter> Filters => filters;

  /// <summary>True when the segment selects visitors or sessions without such an event.</summary>
  public bool IsExcluded { get; }

  /// <summary>
  /// Visitors who had an event matching the filters in the query range.
  /// </summary>
  public static Segment VisitorsWho(string eventName, params Filter[] filters) =>
      new(SegmentScope.Visitors, eventName, filters, false);

  /// <summary>
  /// Sessions that contain an event matching the filters.
  /// </summary>
  public static Segment SessionsWith(string eventName, params Filter[] filters) =>
      new(SegmentScope.Sessions, eventName, filters, false);

  /// <summary>
  /// Visitors who viewed a page.
  /// </summary>
  public static Segment VisitorsWhoViewed(string page) =>
      VisitorsWho(StandardEvents.PageView, Filter.Equal(StandardDimensions.Page, page));

  /// <summary>
  /// The complement: visitors or sessions without a matching event.
  /// </summary>
  public Segment Exclude() => new(Scope, Event, filters, !IsExcluded);

  internal bool Matches(RawEvent rawEvent) => rawEvent.Matches(Event, filters);

  /// <summary>
  /// The text form, which <see cref="Parse"/> reads back, for example
  /// <c>visitors with purchase where plan==pro; country==DE</c>.
  /// </summary>
  public override string ToString()
  {
    var text = new StringBuilder();
    if (IsExcluded)
      text.Append("not ");
    text.Append(Scope == SegmentScope.Visitors ? "visitors" : "sessions").Append(" with ").Append(QuoteEvent(Event));
    if (filters.Length > 0)
      text.Append(" where ").AppendJoin("; ", filters.Select(x => x.ToString()));
    return text.ToString();
  }

  /// <summary>
  /// Parses a segment: <c>visitors</c> or <c>sessions</c>, <c>with</c> and an event name, and
  /// optionally <c>where</c> and filters separated by <c>;</c> (see <see cref="Filter.Parse"/>),
  /// for example <c>visitors with purchase where plan==pro; country==DE</c>. A leading <c>not</c>
  /// excludes the matching visitors or sessions. Event names with spaces, quotes or <c>;</c> are
  /// written in double quotes, with <c>\"</c> and <c>\\</c> for quotes and backslashes.
  /// </summary>
  /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
  /// <exception cref="FormatException">The text is not a segment.</exception>
  public static Segment Parse(string text) => Parsing.Parse(text, "segment", ParseCore);

  /// <summary>
  /// Tries to parse a segment. See <see cref="Parse"/>.
  /// </summary>
  public static bool TryParse(string text, out Segment segment) => Parsing.TryParse(text, Parse, out segment);

  static Segment ParseCore(string text)
  {
    var rest = text;
    var excluded = TakeWord(ref rest, "not");
    var scope = TakeWord(ref rest, "visitors") ? SegmentScope.Visitors :
        TakeWord(ref rest, "sessions") ? SegmentScope.Sessions :
        throw new FormatException($"'{text}' is not a segment: it must start with 'visitors' or 'sessions'.");
    if (!TakeWord(ref rest, "with"))
      throw new FormatException($"'{text}' is not a segment: the scope must be followed by 'with' and an event name.");
    rest = rest.TrimStart();
    string eventName;
    if (rest.StartsWith('"'))
    {
      eventName = ReadQuoted(ref rest, text);
    }
    else
    {
      var where = rest.IndexOf(" where ", StringComparison.OrdinalIgnoreCase);
      eventName = (where < 0 ? rest : rest[..where]).Trim();
      rest = where < 0 ? "" : rest[where..];
    }
    Filter[] conditions = [];
    if (rest.Trim().Length > 0)
    {
      if (!TakeWord(ref rest, "where"))
        throw new FormatException($"'{text}' is not a segment: the event name must be followed by 'where' and filters.");
      conditions = [.. SplitFilters(rest).Select(Filter.Parse)];
    }
    var segment = new Segment(scope, eventName, conditions, false);
    return excluded ? segment.Exclude() : segment;
  }

  static bool TakeWord(ref string text, string word)
  {
    var trimmed = text.TrimStart();
    if (!trimmed.StartsWith(word, StringComparison.OrdinalIgnoreCase) ||
        (trimmed.Length > word.Length && !char.IsWhiteSpace(trimmed[word.Length])))
      return false;
    text = trimmed[word.Length..];
    return true;
  }

  static string QuoteEvent(string name) =>
      name.Any(c => char.IsWhiteSpace(c) || c is '"' or ';' or '\\')
          ? "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
          : name;

  static string ReadQuoted(ref string rest, string text)
  {
    var value = new StringBuilder();
    for (var i = 1; i < rest.Length; ++i)
    {
      var c = rest[i];
      if (c == '\\' && i + 1 < rest.Length)
      {
        value.Append(rest[++i]);
        continue;
      }
      if (c == '"')
      {
        rest = rest[(i + 1)..];
        return value.ToString();
      }
      value.Append(c);
    }
    throw new FormatException($"'{text}' is not a segment: the quoted event name is not closed.");
  }

  /// <summary>
  /// Splits filters at the semicolons that are not escaped as <c>\;</c>.
  /// </summary>
  static IEnumerable<string> SplitFilters(string text)
  {
    var current = new StringBuilder();
    for (var i = 0; i < text.Length; ++i)
    {
      var c = text[i];
      if (c == '\\' && i + 1 < text.Length && text[i + 1] is '|' or ';' or '\\')
      {
        current.Append(c).Append(text[++i]);
        continue;
      }
      if (c == ';')
      {
        yield return current.ToString();
        current.Clear();
        continue;
      }
      current.Append(c);
    }
    yield return current.ToString();
  }
}
