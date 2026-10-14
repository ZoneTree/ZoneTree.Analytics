var request = new WebReportRequest
{
    Range = DateRange.LastDays(30),
    Compare = ComparisonPeriod.PreviousPeriod,
    Filters = { Filter.Equal(StandardDimensions.Country, "DE") },
};

var summary = await analytics.Web.GetSummaryAsync(request);
var daily = await analytics.Web.GetTimeseriesAsync(request, TimeInterval.Day);
var pages = await analytics.Web.GetPagesAsync(request);
var sources = await analytics.Web.GetBreakdownAsync(
    request, StandardDimensions.Source);
var goals = await analytics.Web.GetGoalsAsync(request);

