var today = DateRange.Today;
var completedWeek = DateRange.LastDays(7, includeToday: false);
var recentActivity = DateRange.Last(TimeSpan.FromMinutes(30));
var calendarPeriod = DateRange.Days(
    new DateOnly(2026, 6, 14), new DateOnly(2026, 7, 15));

var query = new AnalyticsQuery
{
    Range = calendarPeriod,
    TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"),
    Compare = ComparisonPeriod.PreviousPeriod,
};

