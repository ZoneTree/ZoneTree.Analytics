using ZoneTree.Analytics.Ingestion;

namespace ZoneTree.Analytics.Web;

/// <summary>
/// Headline web metrics for a range.
/// </summary>
/// <param name="Visitors">Unique visitors with at least one page view.</param>
/// <param name="PageViews">Page views.</param>
/// <param name="Sessions">Sessions started in the range. Null when sessions are unavailable for the filters.</param>
/// <param name="BounceRate">Share of single-interaction sessions, 0 to 1.</param>
/// <param name="AverageSessionDuration">Average session duration.</param>
/// <param name="PageViewsPerSession">Average page views per session.</param>
public sealed record WebSummary(
    long Visitors,
    long PageViews,
    long? Sessions,
    double? BounceRate,
    TimeSpan? AverageSessionDuration,
    double? PageViewsPerSession);

/// <summary>
/// One time series point of headline web metrics.
/// </summary>
/// <param name="Bucket">Bucket start.</param>
/// <param name="Summary">Metrics of the bucket.</param>
public sealed record WebTimeseriesPoint(DateTimeOffset Bucket, WebSummary Summary);

/// <summary>
/// Page statistics.
/// </summary>
/// <param name="Page">The page path.</param>
/// <param name="Visitors">Unique visitors of the page.</param>
/// <param name="PageViews">Page views.</param>
/// <param name="Entrances">Sessions that started on the page.</param>
/// <param name="Exits">Sessions that ended on the page.</param>
public sealed record PageStats(string Page, long Visitors, long PageViews, long Entrances, long Exits)
{
  /// <summary>Exits per page view, 0 to 1.</summary>
  public double ExitRate => PageViews == 0 ? 0 : Math.Min(1, (double)Exits / PageViews);
}

/// <summary>
/// Session statistics grouped by a dimension value.
/// </summary>
/// <param name="Value">The dimension value. Empty means a known empty value, for example direct traffic.</param>
/// <param name="Visitors">Unique visitors who started sessions.</param>
/// <param name="Sessions">Sessions.</param>
/// <param name="BounceRate">Share of single-interaction sessions, 0 to 1.</param>
/// <param name="AverageSessionDuration">Average session duration.</param>
/// <param name="PageViewsPerSession">Average page views per session.</param>
public sealed record SessionBreakdownRow(
    string Value,
    long Visitors,
    long Sessions,
    double BounceRate,
    TimeSpan AverageSessionDuration,
    double PageViewsPerSession);

/// <summary>
/// Custom event statistics.
/// </summary>
/// <param name="Name">The event name.</param>
/// <param name="Events">Number of events.</param>
/// <param name="Visitors">Unique visitors.</param>
public sealed record EventStats(string Name, long Events, long Visitors);

/// <summary>
/// Ready-made web analytics reports built on <see cref="AnalyticsEngine.Query"/>.
/// </summary>
/// <remarks>
/// Every method accepts optional filters. Session metrics (sessions, bounce rate,
/// duration) can only be filtered by session-level dimensions and the entry page;
/// with other filters they are reported as null or omitted.
/// </remarks>
public sealed class WebAnalytics
{
  readonly AnalyticsEngine engine;

  internal WebAnalytics(AnalyticsEngine engine)
  {
    this.engine = engine;
  }

  static bool IsSessionFilterable(IEnumerable<DimensionFilter> filters) =>
      filters == null || filters.All(x =>
          x.Dimension == StandardDimensions.EntryPage || EventEnricher.IsSessionLevel(x.Dimension));

  static AnalyticsQuery NewQuery(
      string eventName, DateTime from, DateTime to, string site, IEnumerable<DimensionFilter> filters)
  {
    var query = new AnalyticsQuery
    {
      Site = site,
      Event = eventName,
      From = from,
      To = to
    };
    if (filters != null)
      query.Filters.AddRange(filters);
    return query;
  }

  static WebSummary ToSummary(AnalyticsRow pageViews, AnalyticsRow sessions)
  {
    long? sessionCount = null;
    double? bounceRate = null;
    TimeSpan? duration = null;
    double? perSession = null;
    if (sessions != null)
    {
      sessionCount = sessions.Events;
      if (sessions.Events > 0)
      {
        bounceRate = Math.Clamp(sessions.Sum(StandardMetrics.Bounces) / sessions.Events, 0, 1);
        duration = TimeSpan.FromSeconds(Math.Max(0, sessions.Sum(StandardMetrics.Duration) / sessions.Events));
        perSession = sessions.Sum(StandardMetrics.SessionPageViews) / sessions.Events;
      }
      else
      {
        bounceRate = 0;
        duration = TimeSpan.Zero;
        perSession = 0;
      }
    }
    return new WebSummary(
        pageViews?.Visitors ?? 0,
        pageViews?.Events ?? 0,
        sessionCount,
        bounceRate,
        duration,
        perSession);
  }

  /// <summary>
  /// Returns headline metrics for [from, to).
  /// </summary>
  public WebSummary GetSummary(
      DateTime from, DateTime to, string site = null, IEnumerable<DimensionFilter> filters = null)
  {
    var filterList = filters?.ToList();
    var pageViews = engine.Query(NewQuery(StandardEvents.PageView, from, to, site, filterList)).Totals;
    AnalyticsRow sessions = null;
    if (engine.Options.Sessions.Enabled && IsSessionFilterable(filterList))
      sessions = engine.Query(NewQuery(StandardEvents.Session, from, to, site, filterList)).Totals;
    return ToSummary(pageViews, sessions);
  }

  /// <summary>
  /// Returns headline metrics per bucket of [from, to).
  /// </summary>
  public IReadOnlyList<WebTimeseriesPoint> GetTimeseries(
      DateTime from,
      DateTime to,
      TimeInterval interval,
      string site = null,
      IEnumerable<DimensionFilter> filters = null)
  {
    if (interval == TimeInterval.None)
      throw new ArgumentException("A time series interval is required.", nameof(interval));
    var filterList = filters?.ToList();
    var pageViewQuery = NewQuery(StandardEvents.PageView, from, to, site, filterList);
    pageViewQuery.Interval = interval;
    var pageViews = engine.Query(pageViewQuery).Rows;
    Dictionary<DateTimeOffset, AnalyticsRow> sessions = null;
    if (engine.Options.Sessions.Enabled && IsSessionFilterable(filterList))
    {
      var sessionQuery = NewQuery(StandardEvents.Session, from, to, site, filterList);
      sessionQuery.Interval = interval;
      sessions = engine.Query(sessionQuery).Rows.ToDictionary(x => x.Bucket.Value);
    }
    var result = new List<WebTimeseriesPoint>(pageViews.Count);
    foreach (var row in pageViews)
    {
      AnalyticsRow sessionRow = null;
      if (sessions != null && !sessions.TryGetValue(row.Bucket.Value, out sessionRow))
        sessionRow = EmptyRow;
      result.Add(new WebTimeseriesPoint(row.Bucket.Value, ToSummary(row, sessionRow)));
    }
    return result;
  }

  static readonly AnalyticsRow EmptyRow = new(null, [], 0, 0, null);

  /// <summary>
  /// Returns the most viewed pages.
  /// </summary>
  public IReadOnlyList<PageStats> GetTopPages(
      DateTime from,
      DateTime to,
      int limit = 10,
      string site = null,
      IEnumerable<DimensionFilter> filters = null)
  {
    var query = NewQuery(StandardEvents.PageView, from, to, site, filters)
        .GroupBy(StandardDimensions.Page)
        .Order(QueryOrder.ByEvents())
        .Top(limit);
    return engine.Query(query).Rows.Select(ToPageStats).ToList();
  }

  /// <summary>
  /// Returns the pages where most sessions ended.
  /// </summary>
  public IReadOnlyList<PageStats> GetExitPages(
      DateTime from,
      DateTime to,
      int limit = 10,
      string site = null,
      IEnumerable<DimensionFilter> filters = null)
  {
    var query = NewQuery(StandardEvents.PageView, from, to, site, filters)
        .GroupBy(StandardDimensions.Page)
        .Order(QueryOrder.ByMetricSum(StandardMetrics.Exits));
    return engine.Query(query).Rows
        .Select(ToPageStats)
        .Where(x => x.Exits > 0)
        .Take(limit)
        .ToList();
  }

  static PageStats ToPageStats(AnalyticsRow row) => new(
      row.Dimensions[0],
      row.Visitors,
      row.Events,
      (long)Math.Round(row.Sum(StandardMetrics.Entrances)),
      (long)Math.Round(row.Sum(StandardMetrics.Exits)));

  /// <summary>
  /// Returns session statistics grouped by a session-level dimension or the entry page.
  /// </summary>
  public IReadOnlyList<SessionBreakdownRow> GetSessionBreakdown(
      string dimension,
      DateTime from,
      DateTime to,
      int limit = 10,
      string site = null,
      IEnumerable<DimensionFilter> filters = null)
  {
    if (dimension != StandardDimensions.EntryPage && !EventEnricher.IsSessionLevel(dimension))
      throw new ArgumentException(
          $"'{dimension}' is not a session-level dimension. Use AnalyticsEngine.Query for event-level breakdowns.",
          nameof(dimension));
    var filterList = filters?.ToList();
    if (!IsSessionFilterable(filterList))
      throw new ArgumentException("Session breakdowns can only be filtered by session-level dimensions.", nameof(filters));
    var query = NewQuery(StandardEvents.Session, from, to, site, filterList)
        .GroupBy(dimension)
        .Order(QueryOrder.ByVisitors())
        .Top(limit);
    return engine.Query(query).Rows.Select(row =>
    {
      var sessions = row.Events;
      return new SessionBreakdownRow(
          row.Dimensions[0],
          row.Visitors,
          sessions,
          sessions == 0 ? 0 : Math.Clamp(row.Sum(StandardMetrics.Bounces) / sessions, 0, 1),
          sessions == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, row.Sum(StandardMetrics.Duration) / sessions)),
          sessions == 0 ? 0 : row.Sum(StandardMetrics.SessionPageViews) / sessions);
    }).ToList();
  }

  /// <summary>Top entry pages.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetEntryPages(DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.EntryPage, from, to, limit, site, filters);

  /// <summary>Top traffic sources.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetSources(DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.Source, from, to, limit, site, filters);

  /// <summary>Top referrer hosts.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetReferrers(DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.Referrer, from, to, limit, site, filters);

  /// <summary>Acquisition channels.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetChannels(DateTime from, DateTime to, int limit = 20, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.Channel, from, to, limit, site, filters);

  /// <summary>Top campaigns by a UTM dimension, for example <see cref="StandardDimensions.UtmCampaign"/>.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetCampaigns(string utmDimension, DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(utmDimension, from, to, limit, site, filters);

  /// <summary>Top countries.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetCountries(DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.Country, from, to, limit, site, filters);

  /// <summary>Top regions.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetRegions(DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.Region, from, to, limit, site, filters);

  /// <summary>Top cities.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetCities(DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.City, from, to, limit, site, filters);

  /// <summary>Browsers.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetBrowsers(DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.Browser, from, to, limit, site, filters);

  /// <summary>Operating systems.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetOperatingSystems(DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.OperatingSystem, from, to, limit, site, filters);

  /// <summary>Device types.</summary>
  public IReadOnlyList<SessionBreakdownRow> GetDevices(DateTime from, DateTime to, int limit = 10, string site = null, IEnumerable<DimensionFilter> filters = null) =>
      GetSessionBreakdown(StandardDimensions.Device, from, to, limit, site, filters);

  /// <summary>
  /// Returns custom events (every event except page views and sessions) with counts and visitors.
  /// </summary>
  public IReadOnlyList<EventStats> GetCustomEvents(
      DateTime from,
      DateTime to,
      int limit = 20,
      string site = null,
      IEnumerable<DimensionFilter> filters = null)
  {
    var filterList = filters?.ToList();
    var result = new List<EventStats>();
    foreach (var name in engine.GetEventNames(from, to, site))
    {
      if (name is StandardEvents.PageView or StandardEvents.Session)
        continue;
      var totals = engine.Query(NewQuery(name, from, to, site, filterList)).Totals;
      if (totals.Events > 0)
        result.Add(new EventStats(name, totals.Events, totals.Visitors));
    }
    return result
        .OrderByDescending(x => x.Events)
        .ThenBy(x => x.Name, StringComparer.Ordinal)
        .Take(limit)
        .ToList();
  }

  /// <summary>
  /// Distinct visitors active in the last <paramref name="window"/> (default 5 minutes).
  /// Real-time; requires raw event storage. Returns null when raw events are not stored.
  /// </summary>
  public long? GetCurrentVisitors(TimeSpan? window = null, string site = null) =>
      engine.GetCurrentVisitors(window ?? TimeSpan.FromMinutes(5), site);
}
