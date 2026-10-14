using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.AspNetCore;

// One mapper per response: normalize floating-point values without changing engine results,
// then build the plan after all rows (including comparisons) have been visited.
sealed class ReportingResponseMapper
{
  bool nonFinite;

  public SummaryResponse Summary(WebSummaryReport report)
  {
    var summary = MapSummary(report.Summary);
    return new(report.From, report.To, summary, MapPlan(report.Plan));
  }

  public ReportResponse<TimeseriesRow> Timeseries(WebReport<WebTimeseriesPoint> report) =>
      MapReport(report, point => new TimeseriesRow(point.Bucket, MapSummary(point.Summary)));

  public ReportResponse<BreakdownRow> Breakdown(WebReport<WebBreakdownRow> report) => MapReport(report, MapBreakdown);

  public ReportResponse<PageRow> Pages(WebReport<PageStats> report) => MapReport(report, MapPage);

  public ReportResponse<GoalRow> Goals(WebReport<GoalStats> report) =>
      MapReport(report, goal => new GoalRow(goal.Name, goal.Visitors, goal.Events, Finite(goal.ConversionRate)));

  public ReportResponse<BotRow> Bots(WebReport<BotStats> report) => MapReport(report, MapBot);

  public QueryResponse Query(AnalyticsResult result)
  {
    var totals = MapQueryRow(result, result.Totals);
    var rows = result.Rows.Select(row => MapQueryRow(result, row)).ToArray();
    return new(result.From, result.To, result.TimeZone.Id, result.Interval.ToString(),
        result.GroupBy, result.Metrics.Select(metric => metric.Key).ToArray(), result.TotalRows,
        totals, rows, MapPlan(result.Plan));
  }

  public FunnelResponse Funnel(FunnelResult result)
  {
    var conversionRate = Finite(result.ConversionRate);
    var steps = result.Steps.Select(step => new FunnelRow(step.Name, step.Visitors,
        Finite(step.ConversionRate), Finite(step.StepConversionRate), step.DropOff)).ToArray();
    return new(result.From, result.To, conversionRate, steps, MapPlan(result.Plan));
  }

  double? Finite(double? value)
  {
    if (!value.HasValue || double.IsFinite(value.Value))
      return value;
    nonFinite = true;
    return null;
  }

  SummaryMetrics MapSummary(WebSummary summary) => new(
      summary.Visitors, summary.PageViews, summary.Sessions, Finite(summary.BounceRate),
      Finite(summary.AverageSessionDuration?.TotalSeconds), Finite(summary.PageViewsPerSession), summary.BotHits,
      summary.Previous == null ? null : MapSummary(summary.Previous));

  BreakdownRow MapBreakdown(WebBreakdownRow row) => new(
      row.Value, row.Visitors, row.PageViews, Finite(row.VisitorShare), row.Sessions,
      Finite(row.BounceRate), Finite(row.AverageSessionDuration?.TotalSeconds),
      row.Previous == null ? null : MapBreakdown(row.Previous));

  PageRow MapPage(PageStats page) => new(
      page.Page, page.Visitors, page.PageViews, page.Entrances, page.Exits,
      Finite(page.ExitRate), page.BotHits, page.Previous == null ? null : MapPage(page.Previous));

  BotRow MapBot(BotStats bot) => new(
      bot.Name, bot.Category, bot.Hits, Finite(bot.Share),
      bot.Previous == null ? null : MapBot(bot.Previous));

  ReportResponse<TResponse> MapReport<T, TResponse>(WebReport<T> report, Func<T, TResponse> mapRow)
  {
    var rows = report.Rows.Select(mapRow).ToArray();
    return new(report.From, report.To, report.TotalRows, rows, MapPlan(report.Plan));
  }

  QueryRow MapQueryRow(AnalyticsResult result, AnalyticsRow row)
  {
    Dictionary<string, string> dimensions = null;
    if (result.GroupBy.Count > 0)
    {
      dimensions = new(result.GroupBy.Count, StringComparer.Ordinal);
      for (var i = 0; i < result.GroupBy.Count; ++i)
        dimensions[result.GroupBy[i]] = row.Dimensions[i];
    }
    var values = new Dictionary<string, double?>(result.Metrics.Count, StringComparer.Ordinal);
    for (var i = 0; i < result.Metrics.Count; ++i)
      values[result.Metrics[i].Key] = Finite(row.Values[i]);
    return new(row.Bucket, dimensions, values,
        row.Previous == null ? null : MapQueryRow(result, row.Previous));
  }

  PlanResponse MapPlan(QueryPlan plan)
  {
    IReadOnlyList<string> warnings = plan.Warnings;
    if (nonFinite)
      warnings = [.. warnings, "Non-finite metric values (overflow or undefined arithmetic) are represented as null in this JSON response."];
    return new(plan.IsComplete && !nonFinite, warnings,
        plan.Steps.Select(step => new PlanStep(step.Event, step.Source.ToString(), step.From, step.To)
            { IndexDimension = step.IndexDimension }).ToArray(),
        plan.RollupSuggestions.Select(suggestion => new RollupSuggestionResponse(
            suggestion.Event, suggestion.Definition.Id, suggestion.Definition.Dimensions, suggestion.Definition.Events)).ToArray());
  }
}
