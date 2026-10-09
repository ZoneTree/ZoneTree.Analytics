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
  Segment(SegmentScope scope, string eventName, Filter[] filters, bool isExcluded)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
    Scope = scope;
    Event = eventName;
    Filters = filters ?? [];
    IsExcluded = isExcluded;
  }

  /// <summary>Visitors or sessions.</summary>
  public SegmentScope Scope { get; }

  /// <summary>The event a visitor or session must have.</summary>
  public string Event { get; }

  /// <summary>Conditions on that event. All must match.</summary>
  public IReadOnlyList<Filter> Filters { get; }

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
  public Segment Exclude() => new(Scope, Event, [.. Filters], !IsExcluded);

  internal bool Matches(string eventName, Func<string, string> dimension)
  {
    if (eventName != Event)
      return false;
    foreach (var filter in Filters)
    {
      if (!filter.IsMatch(dimension(filter.Dimension)))
        return false;
    }
    return true;
  }

  /// <inheritdoc/>
  public override string ToString()
  {
    var prefix = IsExcluded ? "not " : "";
    var conditions = Filters.Count == 0 ? "" : " where " + string.Join(" and ", Filters);
    return $"{prefix}{Scope.ToString().ToLowerInvariant()} with {Event}{conditions}";
  }
}
