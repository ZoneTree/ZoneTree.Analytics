using System.Text.Json.Serialization;

namespace ZoneTree.Analytics.AspNetCore;

// These types define the HTTP contract independently of the engine's result types.
sealed record ErrorResponse(string Error);

sealed record RealtimeResponse(long? Visitors, int Minutes);

sealed record SummaryResponse(DateTimeOffset From, DateTimeOffset To, SummaryMetrics Summary, PlanResponse Plan);

sealed record ReportResponse<T>(DateTimeOffset From, DateTimeOffset To, int TotalRows, T[] Rows, PlanResponse Plan);

sealed record SummaryMetrics(
    long Visitors, long PageViews, long? Sessions, double? BounceRate,
    double? AverageSessionDuration, double? PageViewsPerSession, long? BotHits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SummaryMetrics Previous);

sealed record TimeseriesRow(DateTimeOffset Bucket, SummaryMetrics Summary);

sealed record BreakdownRow(
    string Value, long Visitors, long PageViews, double? VisitorShare, long? Sessions,
    double? BounceRate, double? AverageSessionDuration,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BreakdownRow Previous);

sealed record PageRow(
    string Page, long Visitors, long PageViews, long? Entrances, long? Exits,
    double? ExitRate, long? BotHits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PageRow Previous);

sealed record GoalRow(string Name, long Visitors, long Events, double? ConversionRate);

sealed record BotRow(
    string Name, string Category, long Hits, double? Share,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BotRow Previous);

sealed record QueryResponse(
    DateTimeOffset From, DateTimeOffset To, string TimeZone, string Interval,
    IReadOnlyList<string> GroupBy, string[] Metrics, int TotalRows,
    QueryRow Totals, QueryRow[] Rows, PlanResponse Plan);

sealed record QueryRow(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? Bucket,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string> Dimensions,
    Dictionary<string, double?> Values,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QueryRow Previous);

sealed record FunnelResponse(
    DateTimeOffset From, DateTimeOffset To, double? ConversionRate, FunnelRow[] Steps, PlanResponse Plan);

sealed record FunnelRow(
    string Name, long Visitors, double? ConversionRate, double? StepConversionRate, long DropOff);

sealed record PlanResponse(bool Complete, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notices, PlanStep[] Steps, RollupSuggestionResponse[] RollupSuggestions);

sealed record PlanStep(string Event, string Source, DateTimeOffset From, DateTimeOffset To)
{
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string IndexDimension { get; init; }
}

sealed record RollupSuggestionResponse(string Event, string Id, IReadOnlyList<string> Dimensions, IReadOnlyCollection<string> Events);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(RealtimeResponse))]
[JsonSerializable(typeof(SummaryResponse))]
[JsonSerializable(typeof(ReportResponse<TimeseriesRow>), TypeInfoPropertyName = "TimeseriesResponse")]
[JsonSerializable(typeof(ReportResponse<BreakdownRow>), TypeInfoPropertyName = "BreakdownResponse")]
[JsonSerializable(typeof(ReportResponse<PageRow>), TypeInfoPropertyName = "PagesResponse")]
[JsonSerializable(typeof(ReportResponse<GoalRow>), TypeInfoPropertyName = "GoalsResponse")]
[JsonSerializable(typeof(ReportResponse<BotRow>), TypeInfoPropertyName = "BotsResponse")]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(FunnelResponse))]
sealed partial class ReportingJsonContext : JsonSerializerContext;
