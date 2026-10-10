using ZoneTree.Analytics.Ingestion;

namespace ZoneTree.Analytics.Web;

/// <summary>
/// The scope of a web report.
/// </summary>
public sealed class WebReportRequest
{
  /// <summary>The site. Defaults to <see cref="AnalyticsOptions.DefaultSite"/>.</summary>
  public string Site { get; set; }

  /// <summary>The time range. Default is the last 30 days including today.</summary>
  public DateRange Range { get; set; } = DateRange.LastDays(30);

  /// <summary>The time zone. Defaults to the engine's reporting time zone.</summary>
  public TimeZoneInfo TimeZone { get; set; }

  /// <summary>Dimension filters.</summary>
  public List<Filter> Filters { get; } = [];

  /// <summary>Behavioral segments, for example visitors who signed up.</summary>
  public List<Segment> Segments { get; } = [];

  /// <summary>An optional comparison period. Results then carry <c>Previous</c> values.</summary>
  public Comparison Compare { get; set; }

  /// <summary>Maximum number of rows of list reports. Default is 10; null returns every row.</summary>
  public int? Limit { get; set; } = 10;

  /// <summary>Rows to skip, for paging list reports.</summary>
  public int Offset { get; set; }
}

/// <summary>
/// Headline web metrics.
/// </summary>
/// <param name="Visitors">Unique visitors with at least one page view.</param>
/// <param name="PageViews">Page views.</param>
/// <param name="Sessions">Sessions started. Null when the filters exclude session metrics.</param>
/// <param name="BounceRate">Share of sessions with at most one interactive event, 0 to 1.</param>
/// <param name="AverageSessionDuration">Average session duration.</param>
/// <param name="PageViewsPerSession">Average page views per session.</param>
/// <param name="BotHits">Bot requests, side by side with page views. Null unless
/// <see cref="WebOptions.Bots"/> is <see cref="BotTraffic.TrackSeparately"/>, and null when the
/// request has segments or filters other than page and hostname.</param>
public sealed record WebSummary(
    long Visitors,
    long PageViews,
    long? Sessions,
    double? BounceRate,
    TimeSpan? AverageSessionDuration,
    double? PageViewsPerSession,
    long? BotHits)
{
  /// <summary>The comparison period, when requested.</summary>
  public WebSummary Previous { get; init; }
}

/// <summary>
/// One time series point.
/// </summary>
/// <param name="Bucket">The bucket start.</param>
/// <param name="Summary">The metrics of the bucket.</param>
public sealed record WebTimeseriesPoint(DateTimeOffset Bucket, WebSummary Summary);

/// <summary>
/// A breakdown row.
/// </summary>
/// <param name="Value">The dimension value. Null means the page views do not carry the dimension.</param>
/// <param name="Visitors">Unique visitors with a page view.</param>
/// <param name="PageViews">Page views.</param>
/// <param name="VisitorShare">This row's share of all visitors, 0 to 1.</param>
/// <param name="Sessions">Sessions. Null when the dimension or filters are not session-level.</param>
/// <param name="BounceRate">Bounce rate, 0 to 1.</param>
/// <param name="AverageSessionDuration">Average session duration.</param>
public sealed record WebBreakdownRow(
    string Value,
    long Visitors,
    long PageViews,
    double VisitorShare,
    long? Sessions,
    double? BounceRate,
    TimeSpan? AverageSessionDuration)
{
  /// <summary>The same row of the comparison period, when requested.</summary>
  public WebBreakdownRow Previous { get; init; }
}

/// <summary>
/// Page statistics.
/// </summary>
/// <param name="Page">The page path.</param>
/// <param name="Visitors">Unique visitors of the page.</param>
/// <param name="PageViews">Page views.</param>
/// <param name="Entrances">Sessions that started on the page. Null when filtered by custom dimensions.</param>
/// <param name="Exits">Sessions that ended on the page.</param>
/// <param name="ExitRate">Exits per page view, 0 to 1.</param>
/// <param name="BotHits">Bot requests of the page. Null unless bots are tracked separately, and
/// null when the request has segments or filters other than page and hostname.</param>
public sealed record PageStats(
    string Page,
    long Visitors,
    long PageViews,
    long? Entrances,
    long? Exits,
    double? ExitRate,
    long? BotHits)
{
  /// <summary>The same page in the comparison period, when requested.</summary>
  public PageStats Previous { get; init; }
}

/// <summary>
/// Goal (custom event) statistics.
/// </summary>
/// <param name="Name">The event name.</param>
/// <param name="Visitors">Unique visitors who completed the goal.</param>
/// <param name="Events">Number of events.</param>
/// <param name="ConversionRate">Goal visitors divided by visitors with a page view, 0 to 1.</param>
public sealed record GoalStats(string Name, long Visitors, long Events, double ConversionRate);

/// <summary>
/// Bot statistics.
/// </summary>
/// <param name="Name">The bot name, for example "Googlebot" or "GPTBot".</param>
/// <param name="Category">The bot category, see <see cref="BotCategories"/>.</param>
/// <param name="Hits">Requests made by the bot.</param>
/// <param name="Share">This bot's share of all bot requests, 0 to 1.</param>
public sealed record BotStats(string Name, string Category, long Hits, double Share)
{
  /// <summary>The same bot in the comparison period, when requested.</summary>
  public BotStats Previous { get; init; }
}

/// <summary>
/// A list report.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
/// <param name="Rows">The rows after offset and limit.</param>
/// <param name="TotalRows">The number of rows before offset and limit.</param>
/// <param name="Plan">How the report was answered.</param>
public sealed record WebReport<T>(IReadOnlyList<T> Rows, int TotalRows, QueryPlan Plan);

/// <summary>
/// Ready-made web analytics reports.
/// </summary>
/// <remarks>
/// <para>
/// Every report uses the same definitions: visitors are unique visitors with at least one page
/// view, so numbers add up across reports. Session metrics (sessions, bounce rate, duration) are
/// reported when the breakdown dimension and filters are session-level (source, campaign,
/// geography, device, entry page); otherwise they are null.
/// </para>
/// <para>
/// When <see cref="WebOptions.Bots"/> is <see cref="BotTraffic.TrackSeparately"/>, the summary,
/// time series and page reports carry bot hits next to page views, and
/// <see cref="GetBots"/> lists the bots. Bot traffic never counts as visitors or page views.
/// </para>
/// <para>
/// Reports are built on <see cref="AnalyticsEngine.Query(AnalyticsQuery, CancellationToken)"/>;
/// use it directly for anything these reports do not cover. Every report has a synchronous
/// method and an asynchronous one, which runs it on a thread-pool thread.
/// </para>
/// </remarks>
public sealed class WebAnalytics
{
  readonly AnalyticsEngine engine;

  internal WebAnalytics(AnalyticsEngine engine)
  {
    this.engine = engine;
  }

  static readonly Metric[] SessionMetrics =
  [
    Metric.Sessions,
    Metric.BounceRate,
    Metric.AverageSessionDuration,
    Metric.PageViewsPerSession,
  ];

  bool SessionMetricsAvailable(WebReportRequest request, string dimension = null)
  {
    if (!engine.Options.Sessions.Enabled)
      return false;
    var schema = engine.Schema;
    return (dimension == null || schema.Contains(DimensionScope.Session, dimension)) &&
        request.Filters.All(x => x == null || schema.Contains(DimensionScope.Session, x.Dimension));
  }

  bool BotMetricsAvailable(WebReportRequest request) =>
      engine.Options.Web.Bots == BotTraffic.TrackSeparately &&
      request.Segments.Count == 0 &&
      request.Filters.All(x => x == null || x.Dimension is StandardDimensions.Page or StandardDimensions.Hostname);

  bool PageMetricsAvailable(WebReportRequest request) =>
      request.Filters.All(x => x == null || engine.Schema.Contains(DimensionScope.PageView, x.Dimension));

  static AnalyticsQuery NewQuery(WebReportRequest request, bool paged)
  {
    ArgumentNullException.ThrowIfNull(request);
    var query = new AnalyticsQuery
    {
      Site = request.Site,
      Event = StandardEvents.PageView,
      Range = request.Range,
      TimeZone = request.TimeZone,
      Compare = request.Compare,
      IncludeRowsWithoutBaseEvent = false
    };
    query.Filters.AddRange(request.Filters);
    query.Segments.AddRange(request.Segments);
    if (paged)
    {
      query.Limit = request.Limit;
      query.Offset = request.Offset;
    }
    return query;
  }

  static void AddSummaryMetrics(AnalyticsQuery query, bool sessions, bool bots)
  {
    query.Metrics.Add(Metric.Visitors);
    query.Metrics.Add(Metric.PageViews);
    if (sessions)
      query.Metrics.AddRange(SessionMetrics);
    if (bots)
      query.Metrics.Add(Metric.BotHits);
  }

  static WebSummary ToSummary(AnalyticsRow row, bool sessions, bool bots)
  {
    if (row == null)
      return null;
    return new WebSummary(
        (long)row[Metric.Visitors],
        (long)row[Metric.PageViews],
        sessions ? (long)row[Metric.Sessions] : null,
        sessions ? row[Metric.BounceRate] : null,
        sessions ? TimeSpan.FromSeconds(row[Metric.AverageSessionDuration]) : null,
        sessions ? row[Metric.PageViewsPerSession] : null,
        bots ? (long)row[Metric.BotHits] : null)
    {
      Previous = ToSummary(row.Previous, sessions, bots)
    };
  }

  /// <summary>
  /// Headline metrics of the range.
  /// </summary>
  /// <inheritdoc cref="AnalyticsEngine.Query(AnalyticsQuery, CancellationToken)" path="/exception"/>
  public WebSummary GetSummary(WebReportRequest request, CancellationToken cancellationToken = default)
  {
    var sessions = SessionMetricsAvailable(request);
    var bots = BotMetricsAvailable(request);
    var query = NewQuery(request, paged: false);
    AddSummaryMetrics(query, sessions, bots);
    var result = engine.Query(query, cancellationToken);
    return ToSummary(result.Totals, sessions, bots);
  }

  /// <inheritdoc cref="GetSummary"/>
  public Task<WebSummary> GetSummaryAsync(WebReportRequest request, CancellationToken cancellationToken = default) =>
      Task.Run(() => GetSummary(request, cancellationToken), cancellationToken);

  /// <summary>
  /// Headline metrics per time bucket.
  /// </summary>
  /// <inheritdoc cref="AnalyticsEngine.Query(AnalyticsQuery, CancellationToken)" path="/exception"/>
  public IReadOnlyList<WebTimeseriesPoint> GetTimeseries(
      WebReportRequest request, TimeInterval interval, CancellationToken cancellationToken = default)
  {
    if (interval == TimeInterval.None)
      throw new ArgumentException("A time series interval is required.", nameof(interval));
    var sessions = SessionMetricsAvailable(request);
    var bots = BotMetricsAvailable(request);
    var query = NewQuery(request, paged: false);
    query.Interval = interval;
    AddSummaryMetrics(query, sessions, bots);
    var result = engine.Query(query, cancellationToken);
    return result.Rows.Select(x => new WebTimeseriesPoint(x.Bucket.Value, ToSummary(x, sessions, bots))).ToList();
  }

  /// <inheritdoc cref="GetTimeseries"/>
  public Task<IReadOnlyList<WebTimeseriesPoint>> GetTimeseriesAsync(
      WebReportRequest request, TimeInterval interval, CancellationToken cancellationToken = default) =>
      Task.Run(() => GetTimeseries(request, interval, cancellationToken), cancellationToken);

  /// <summary>
  /// Visitors, page views and, for session-level dimensions, session metrics grouped by a dimension,
  /// ordered by visitors. Typical dimensions: <see cref="StandardDimensions.Source"/>,
  /// <see cref="StandardDimensions.Channel"/>, <see cref="StandardDimensions.Referrer"/>,
  /// <see cref="StandardDimensions.UtmCampaign"/>, <see cref="StandardDimensions.Country"/>,
  /// <see cref="StandardDimensions.Device"/>, <see cref="StandardDimensions.Browser"/>,
  /// <see cref="StandardDimensions.EntryPage"/> or any custom dimension.
  /// </summary>
  /// <inheritdoc cref="AnalyticsEngine.Query(AnalyticsQuery, CancellationToken)" path="/exception"/>
  public WebReport<WebBreakdownRow> GetBreakdown(
      WebReportRequest request, string dimension, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
    var sessions = SessionMetricsAvailable(request, dimension);
    var query = NewQuery(request, paged: true);
    query.GroupBy.Add(dimension);
    query.Metrics.Add(Metric.Visitors);
    query.Metrics.Add(Metric.PageViews);
    query.Metrics.Add(Metric.PercentOfTotal(Metric.Visitors));
    if (sessions)
    {
      query.Metrics.Add(Metric.Sessions);
      query.Metrics.Add(Metric.BounceRate);
      query.Metrics.Add(Metric.AverageSessionDuration);
    }
    query.OrderBy.Add(Order.By(Metric.Visitors));
    query.OrderBy.Add(Order.By(Metric.PageViews));
    var result = engine.Query(query, cancellationToken);

    WebBreakdownRow ToRow(AnalyticsRow row) => row == null ? null : new WebBreakdownRow(
        row.Dimensions[0],
        (long)row[Metric.Visitors],
        (long)row[Metric.PageViews],
        row[Metric.PercentOfTotal(Metric.Visitors)],
        sessions ? (long)row[Metric.Sessions] : null,
        sessions ? row[Metric.BounceRate] : null,
        sessions ? TimeSpan.FromSeconds(row[Metric.AverageSessionDuration]) : null)
    {
      Previous = ToRow(row.Previous)
    };
    return new WebReport<WebBreakdownRow>(result.Rows.Select(x => ToRow(x)).ToList(), result.TotalRows, result.Plan);
  }

  /// <inheritdoc cref="GetBreakdown"/>
  public Task<WebReport<WebBreakdownRow>> GetBreakdownAsync(
      WebReportRequest request, string dimension, CancellationToken cancellationToken = default) =>
      Task.Run(() => GetBreakdown(request, dimension, cancellationToken), cancellationToken);

  /// <summary>
  /// Pages ordered by page views, with entrances, exits, exit rate and, when bots are tracked
  /// separately, bot hits. Pages visited only by bots are not listed.
  /// </summary>
  /// <inheritdoc cref="AnalyticsEngine.Query(AnalyticsQuery, CancellationToken)" path="/exception"/>
  public WebReport<PageStats> GetPages(WebReportRequest request, CancellationToken cancellationToken = default) =>
      GetPages(request, Metric.PageViews, cancellationToken);

  /// <inheritdoc cref="GetPages(WebReportRequest, CancellationToken)"/>
  public Task<WebReport<PageStats>> GetPagesAsync(WebReportRequest request, CancellationToken cancellationToken = default) =>
      Task.Run(() => GetPages(request, cancellationToken), cancellationToken);

  /// <summary>
  /// Pages where most sessions ended.
  /// </summary>
  /// <inheritdoc cref="AnalyticsEngine.Query(AnalyticsQuery, CancellationToken)" path="/exception"/>
  public WebReport<PageStats> GetExitPages(WebReportRequest request, CancellationToken cancellationToken = default) =>
      GetPages(request, Metric.Exits, cancellationToken);

  /// <inheritdoc cref="GetExitPages"/>
  public Task<WebReport<PageStats>> GetExitPagesAsync(WebReportRequest request, CancellationToken cancellationToken = default) =>
      Task.Run(() => GetExitPages(request, cancellationToken), cancellationToken);

  WebReport<PageStats> GetPages(WebReportRequest request, Metric order, CancellationToken cancellationToken)
  {
    var pageMetrics = PageMetricsAvailable(request);
    var bots = BotMetricsAvailable(request);
    if (!pageMetrics && order == Metric.Exits)
      throw new ArgumentException("Exit pages cannot be filtered by custom dimensions.", nameof(request));
    var query = NewQuery(request, paged: true);
    query.GroupBy.Add(StandardDimensions.Page);
    query.Metrics.Add(Metric.Visitors);
    query.Metrics.Add(Metric.PageViews);
    if (pageMetrics)
    {
      query.Metrics.Add(Metric.Entrances);
      query.Metrics.Add(Metric.Exits);
      query.Metrics.Add(Metric.ExitRate);
    }
    if (bots)
      query.Metrics.Add(Metric.BotHits);
    query.OrderBy.Add(Order.By(order));
    query.OrderBy.Add(Order.By(Metric.Visitors));
    var result = engine.Query(query, cancellationToken);

    PageStats ToStats(AnalyticsRow row) => row == null ? null : new PageStats(
        row.Dimensions[0],
        (long)row[Metric.Visitors],
        (long)row[Metric.PageViews],
        pageMetrics ? (long)row[Metric.Entrances] : null,
        pageMetrics ? (long)row[Metric.Exits] : null,
        pageMetrics ? row[Metric.ExitRate] : null,
        bots ? (long)row[Metric.BotHits] : null)
    {
      Previous = ToStats(row.Previous)
    };
    var rows = result.Rows.Where(x => x.Dimensions[0] != null);
    if (order == Metric.Exits)
      rows = rows.Where(x => x[Metric.Exits] > 0);
    return new WebReport<PageStats>(rows.Select(x => ToStats(x)).ToList(), result.TotalRows, result.Plan);
  }

  /// <summary>
  /// Custom events with visitors, events and conversion rate, ordered by visitors.
  /// </summary>
  /// <inheritdoc cref="AnalyticsEngine.Query(AnalyticsQuery, CancellationToken)" path="/exception"/>
  public WebReport<GoalStats> GetGoals(WebReportRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var goals = engine.GetEventNames(request.Range, request.Site, request.TimeZone, cancellationToken)
        .Where(x => x is not (StandardEvents.PageView or StandardEvents.Session or StandardEvents.Bot))
        .ToList();
    var query = NewQuery(request, paged: false);
    query.Compare = null;
    query.Metrics.Add(Metric.Visitors);
    foreach (var goal in goals)
    {
      query.Metrics.Add(Metric.Conversions(goal));
      query.Metrics.Add(Metric.EventsOf(goal));
      query.Metrics.Add(Metric.ConversionRate(goal));
    }
    var result = engine.Query(query, cancellationToken);
    var totals = result.Totals;
    var rows = goals
        .Select(x => new GoalStats(
            x,
            (long)totals[Metric.Conversions(x)],
            (long)totals[Metric.EventsOf(x)],
            totals[Metric.ConversionRate(x)]))
        .Where(x => x.Events > 0)
        .OrderByDescending(x => x.Visitors)
        .ThenByDescending(x => x.Events)
        .ThenBy(x => x.Name, StringComparer.Ordinal)
        .ToList();
    var page = rows.Skip(request.Offset);
    if (request.Limit is { } limit)
      page = page.Take(limit);
    return new WebReport<GoalStats>(page.ToList(), rows.Count, result.Plan);
  }

  /// <inheritdoc cref="GetGoals"/>
  public Task<WebReport<GoalStats>> GetGoalsAsync(WebReportRequest request, CancellationToken cancellationToken = default) =>
      Task.Run(() => GetGoals(request, cancellationToken), cancellationToken);

  /// <summary>
  /// Bots ordered by hits, with their category and share of all bot hits. Requires
  /// <see cref="WebOptions.Bots"/> to be <see cref="BotTraffic.TrackSeparately"/>; otherwise the
  /// report is empty. Filters can use page, hostname, bot and bot_category; segments are not
  /// supported because bot events carry no visitor.
  /// </summary>
  /// <inheritdoc cref="AnalyticsEngine.Query(AnalyticsQuery, CancellationToken)" path="/exception"/>
  public WebReport<BotStats> GetBots(WebReportRequest request, CancellationToken cancellationToken = default)
  {
    var query = NewQuery(request, paged: true);
    query.Event = StandardEvents.Bot;
    query.GroupBy.Add(StandardDimensions.Bot);
    query.GroupBy.Add(StandardDimensions.BotCategory);
    query.Metrics.Add(Metric.Events);
    query.Metrics.Add(Metric.PercentOfTotal(Metric.Events));
    query.OrderBy.Add(Order.By(Metric.Events));
    var result = engine.Query(query, cancellationToken);

    static BotStats ToStats(AnalyticsRow row) => row == null ? null : new BotStats(
        row.Dimensions[0],
        row.Dimensions[1],
        (long)row[Metric.Events],
        row[Metric.PercentOfTotal(Metric.Events)])
    {
      Previous = ToStats(row.Previous)
    };
    return new WebReport<BotStats>(result.Rows.Select(x => ToStats(x)).ToList(), result.TotalRows, result.Plan);
  }

  /// <inheritdoc cref="GetBots"/>
  public Task<WebReport<BotStats>> GetBotsAsync(WebReportRequest request, CancellationToken cancellationToken = default) =>
      Task.Run(() => GetBots(request, cancellationToken), cancellationToken);

  /// <summary>
  /// Unique visitors active in the last <paramref name="window"/> (default 5 minutes).
  /// Real-time; requires raw event storage. Returns null when raw events are not stored.
  /// </summary>
  public long? GetCurrentVisitors(string site = null, TimeSpan? window = null, CancellationToken cancellationToken = default) =>
      engine.GetCurrentVisitors(window ?? TimeSpan.FromMinutes(5), site, cancellationToken);

  /// <inheritdoc cref="GetCurrentVisitors"/>
  public Task<long?> GetCurrentVisitorsAsync(string site = null, TimeSpan? window = null, CancellationToken cancellationToken = default) =>
      Task.Run(() => GetCurrentVisitors(site, window, cancellationToken), cancellationToken);
}
