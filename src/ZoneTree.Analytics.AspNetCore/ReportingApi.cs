using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.AspNetCore;

/// <summary>
/// JSON reporting API registration.
/// </summary>
public static class AnalyticsReportingApiExtensions
{
  /// <summary>
  /// Maps a read-only JSON reporting API under <paramref name="prefix"/>
  /// (default "{PathPrefix}/api"):
  /// <list type="bullet">
  /// <item><c>GET summary</c>, <c>GET timeseries?interval=day</c>, <c>GET breakdown?dimension=source</c>,
  /// <c>GET pages</c>, <c>GET exit-pages</c>, <c>GET goals</c>, <c>GET bots</c>, <c>GET realtime</c></item>
  /// <item><c>POST query</c> with a JSON query, <c>POST funnel</c> with a JSON funnel</item>
  /// </list>
  /// Common parameters: <c>site</c>, <c>range</c> (today, yesterday, 7d, 30d, month, last-month,
  /// year, or a rolling duration such as 30m or 12h), or <c>from</c> and <c>to</c> (dates are
  /// inclusive days, date-times are an exact range), <c>tz</c>, <c>filter</c> (repeatable filter
  /// expressions, see <see cref="Filter.Parse"/>), <c>compare</c> (previous or year),
  /// <c>limit</c> and <c>offset</c>.
  /// </summary>
  /// <remarks>
  /// The API exposes analytics data: protect it, for example with
  /// <c>.RequireAuthorization()</c> on the returned group, and restrict sites with
  /// <see cref="AnalyticsWebOptions.AuthorizeSite"/>.
  /// </remarks>
  public static RouteGroupBuilder MapZoneTreeAnalyticsApi(this IEndpointRouteBuilder endpoints, string prefix = null)
  {
    ArgumentNullException.ThrowIfNull(endpoints);
    var options = endpoints.ServiceProvider.GetRequiredService<AnalyticsWebOptions>();
    var group = endpoints.MapGroup(prefix ?? options.PathPrefix.TrimEnd('/') + "/api");
    group.MapGet("/summary", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.SummaryAsync)));
    group.MapGet("/timeseries", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.TimeseriesAsync)));
    group.MapGet("/breakdown", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.BreakdownAsync)));
    group.MapGet("/pages", (RequestDelegate)(context => ReportingApi.Run(context, (c, r, w, t) => ReportingApi.PagesAsync(c, r, w, false, t))));
    group.MapGet("/exit-pages", (RequestDelegate)(context => ReportingApi.Run(context, (c, r, w, t) => ReportingApi.PagesAsync(c, r, w, true, t))));
    group.MapGet("/goals", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.GoalsAsync)));
    group.MapGet("/bots", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.BotsAsync)));
    group.MapGet("/realtime", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.RealtimeAsync)));
    group.MapPost("/query", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.QueryAsync)));
    group.MapPost("/funnel", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.FunnelAsync)));
    return group;
  }
}

/// <summary>
/// Request parameters: the JSON body of a POST request, or the query string of a GET request.
/// </summary>
sealed class ApiParameters
{
  readonly IQueryCollection query;

  readonly JsonElement body;

  readonly bool hasBody;

  public ApiParameters(IQueryCollection query, JsonElement? body)
  {
    this.query = query;
    hasBody = body.HasValue && body.Value.ValueKind == JsonValueKind.Object;
    if (hasBody)
      this.body = body.Value;
  }

  public JsonElement? Element(string name) =>
      hasBody && body.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

  public string Get(string name)
  {
    if (!hasBody)
    {
      var values = query[name];
      return values.Count == 0 ? null : values[^1];
    }
    if (Element(name) is not { } element)
      return null;
    return element.ValueKind switch
    {
      JsonValueKind.String => element.GetString(),
      JsonValueKind.Number => element.GetRawText(),
      JsonValueKind.True => "true",
      JsonValueKind.False => "false",
      _ => throw new FormatException($"'{name}' must be a string or a number.")
    };
  }

  public IEnumerable<string> GetAll(string name)
  {
    if (!hasBody)
      return query[name].Where(x => !string.IsNullOrEmpty(x)).SelectMany(x => x.Split(',')).ToList();
    if (Element(name) is not { } element)
      return [];
    if (element.ValueKind == JsonValueKind.String)
      return [element.GetString()];
    if (element.ValueKind != JsonValueKind.Array)
      throw new FormatException($"'{name}' must be an array of strings.");
    return element.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String
        ? x.GetString()
        : throw new FormatException($"'{name}' must be an array of strings.")).ToList();
  }

  public int? GetInt(string name)
  {
    var value = Get(name);
    if (value == null)
      return null;
    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0
        ? result
        : throw new FormatException($"'{name}' must be a non-negative integer.");
  }
}

/// <summary>
/// The reporting API handlers.
/// </summary>
static class ReportingApi
{
  public delegate Task Handler(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken);

  public static async Task Run(HttpContext context, Handler handler)
  {
    var cancellationToken = context.RequestAborted;
    JsonDocument document = null;
    try
    {
      if (HttpMethods.IsPost(context.Request.Method))
      {
        try
        {
          document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException e)
        {
          await WriteError(context, StatusCodes.Status400BadRequest, "The body is not valid JSON: " + e.Message);
          return;
        }
      }
      var parameters = new ApiParameters(context.Request.Query, document?.RootElement);
      var options = context.RequestServices.GetRequiredService<AnalyticsWebOptions>();
      var site = parameters.Get("site");
      if (options.AuthorizeSite != null && !options.AuthorizeSite(context, site))
      {
        await WriteError(context, StatusCodes.Status403Forbidden, "Access to this site is not allowed.");
        return;
      }
      using var buffer = new MemoryStream();
      await using (var writer = new Utf8JsonWriter(buffer))
      {
        await handler(context, parameters, writer, cancellationToken);
      }
      context.Response.ContentType = "application/json; charset=utf-8";
      context.Response.Headers.CacheControl = "no-store";
      buffer.Position = 0;
      await buffer.CopyToAsync(context.Response.Body, cancellationToken);
    }
    catch (Exception e) when (e is AnalyticsQueryException or FormatException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
    {
      await WriteError(context, StatusCodes.Status400BadRequest, e.Message);
    }
    catch (AnalyticsQueryTimeoutException e)
    {
      await WriteError(context, StatusCodes.Status503ServiceUnavailable, e.Message);
    }
    finally
    {
      document?.Dispose();
    }
  }

  static async Task WriteError(HttpContext context, int status, string message)
  {
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json; charset=utf-8";
    using var buffer = new MemoryStream();
    await using (var writer = new Utf8JsonWriter(buffer))
    {
      writer.WriteStartObject();
      writer.WriteString("error", message);
      writer.WriteEndObject();
    }
    buffer.Position = 0;
    await buffer.CopyToAsync(context.Response.Body);
  }

  static AnalyticsEngine Engine(HttpContext context) => context.RequestServices.GetRequiredService<AnalyticsEngine>();

  #region Parameters

  static TimeZoneInfo ParseTimeZone(ApiParameters parameters)
  {
    var id = parameters.Get("tz");
    return string.IsNullOrWhiteSpace(id) ? null : TimeZoneInfo.FindSystemTimeZoneById(id);
  }

  public static DateRange ParseRange(ApiParameters parameters)
  {
    var from = parameters.Get("from");
    var to = parameters.Get("to");
    if (from != null || to != null)
    {
      if (from == null || to == null)
        throw new FormatException("'from' and 'to' must be given together.");
      if (DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var firstDay) &&
          DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastDay))
        return DateRange.Days(firstDay, lastDay);
      if (DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) &&
          DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
        return DateRange.Between(start, end);
      throw new FormatException("'from' and 'to' must both be dates (yyyy-MM-dd) or date-times (ISO 8601).");
    }
    var range = parameters.Get("range")?.Trim().ToLowerInvariant();
    switch (range)
    {
      case null or "" or "30d":
        return DateRange.LastDays(30);
      case "today":
        return DateRange.Today;
      case "yesterday":
        return DateRange.Yesterday;
      case "month" or "this-month":
        return DateRange.ThisMonth;
      case "last-month":
        return DateRange.LastMonth;
      case "year" or "this-year":
        return DateRange.ThisYear;
    }
    if (range.Length > 1 && int.TryParse(range[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0)
    {
      switch (range[^1])
      {
        case 'd':
          return DateRange.LastDays(count);
        case 'h':
          return DateRange.Last(TimeSpan.FromHours(count));
        case 'm':
          return DateRange.Last(TimeSpan.FromMinutes(count));
      }
    }
    throw new FormatException($"Unknown range '{range}'. Use today, yesterday, 7d, 30d, month, last-month, year, 30m, 12h, or from and to.");
  }

  static Comparison ParseComparison(ApiParameters parameters) => parameters.Get("compare")?.Trim().ToLowerInvariant() switch
  {
    null or "" or "none" => null,
    "previous" or "previous-period" => Comparison.PreviousPeriod,
    "year" or "previous-year" => Comparison.PreviousYear,
    var other => throw new FormatException($"Unknown comparison '{other}'. Use previous or year.")
  };

  static TEnum ParseEnum<TEnum>(ApiParameters parameters, string name, TEnum fallback) where TEnum : struct, Enum
  {
    var value = parameters.Get(name);
    if (string.IsNullOrWhiteSpace(value))
      return fallback;
    return Enum.TryParse<TEnum>(value.Replace("-", "").Replace("_", ""), ignoreCase: true, out var result) && Enum.IsDefined(result)
        ? result
        : throw new FormatException($"Unknown {name} '{value}'.");
  }

  static TimeInterval ParseInterval(ApiParameters parameters, TimeInterval fallback)
  {
    var value = parameters.Get("interval");
    return string.IsNullOrWhiteSpace(value) ? fallback : TimeInterval.Parse(value);
  }

  static IEnumerable<Filter> ParseFilters(ApiParameters parameters) =>
      parameters.GetAll("filter").Concat(parameters.GetAll("filters")).Select(Filter.Parse);

  static IEnumerable<Segment> ParseSegments(ApiParameters parameters)
  {
    if (parameters.Element("segments") is not { } element)
      yield break;
    if (element.ValueKind != JsonValueKind.Array)
      throw new FormatException("'segments' must be an array.");
    foreach (var item in element.EnumerateArray())
    {
      var segment = new ApiParameters(new QueryCollection(), item);
      var eventName = segment.Get("event") ?? throw new FormatException("A segment needs an 'event'.");
      var filters = segment.GetAll("filters").Select(Filter.Parse).ToArray();
      var result = segment.Get("scope")?.ToLowerInvariant() switch
      {
        null or "visitors" => Segment.VisitorsWho(eventName, filters),
        "sessions" => Segment.SessionsWith(eventName, filters),
        var other => throw new FormatException($"Unknown segment scope '{other}'. Use visitors or sessions.")
      };
      yield return segment.Get("exclude") == "true" ? result.Exclude() : result;
    }
  }

  static WebReportRequest ParseRequest(ApiParameters parameters)
  {
    var request = new WebReportRequest
    {
      Site = parameters.Get("site"),
      Range = ParseRange(parameters),
      TimeZone = ParseTimeZone(parameters),
      Compare = ParseComparison(parameters),
      Limit = parameters.GetInt("limit") ?? 10,
      Offset = parameters.GetInt("offset") ?? 0
    };
    request.Filters.AddRange(ParseFilters(parameters));
    request.Segments.AddRange(ParseSegments(parameters));
    return request;
  }

  #endregion

  #region Handlers

  public static async Task SummaryAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var summary = await Engine(context).Web.GetSummaryAsync(ParseRequest(parameters), cancellationToken);
    WriteSummary(writer, summary);
  }

  public static async Task TimeseriesAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var interval = ParseInterval(parameters, TimeInterval.Day);
    var points = await Engine(context).Web.GetTimeseriesAsync(ParseRequest(parameters), interval, cancellationToken);
    writer.WriteStartArray();
    foreach (var point in points)
    {
      writer.WriteStartObject();
      writer.WriteString("bucket", point.Bucket);
      writer.WritePropertyName("summary");
      WriteSummary(writer, point.Summary);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();
  }

  public static async Task BreakdownAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var dimension = parameters.Get("dimension") ?? throw new FormatException("'dimension' is required.");
    var report = await Engine(context).Web.GetBreakdownAsync(ParseRequest(parameters), dimension, cancellationToken);
    WriteReport(writer, report, WriteBreakdownRow);
  }

  public static async Task PagesAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, bool exits, CancellationToken cancellationToken)
  {
    var request = ParseRequest(parameters);
    var web = Engine(context).Web;
    var report = exits
        ? await web.GetExitPagesAsync(request, cancellationToken)
        : await web.GetPagesAsync(request, cancellationToken);
    WriteReport(writer, report, WritePageStats);
  }

  public static async Task GoalsAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var report = await Engine(context).Web.GetGoalsAsync(ParseRequest(parameters), cancellationToken);
    WriteReport(writer, report, (w, goal) =>
    {
      w.WriteStartObject();
      w.WriteString("name", goal.Name);
      w.WriteNumber("visitors", goal.Visitors);
      w.WriteNumber("events", goal.Events);
      w.WriteNumber("conversionRate", goal.ConversionRate);
      w.WriteEndObject();
    });
  }

  public static async Task BotsAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var report = await Engine(context).Web.GetBotsAsync(ParseRequest(parameters), cancellationToken);
    WriteReport(writer, report, WriteBotStats);
  }

  public static Task RealtimeAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var minutes = parameters.GetInt("minutes") ?? 5;
    var visitors = Engine(context).Web.GetCurrentVisitors(parameters.Get("site"), TimeSpan.FromMinutes(Math.Max(1, minutes)));
    writer.WriteStartObject();
    if (visitors.HasValue)
      writer.WriteNumber("visitors", visitors.Value);
    else
      writer.WriteNull("visitors");
    writer.WriteNumber("minutes", Math.Max(1, minutes));
    writer.WriteEndObject();
    return Task.CompletedTask;
  }

  public static async Task QueryAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var query = new AnalyticsQuery
    {
      Site = parameters.Get("site"),
      Event = parameters.Get("event") ?? StandardEvents.PageView,
      Range = ParseRange(parameters),
      TimeZone = ParseTimeZone(parameters),
      Interval = ParseInterval(parameters, TimeInterval.None),
      Compare = ParseComparison(parameters),
      Source = ParseEnum(parameters, "source", QuerySource.Auto),
      Fill = ParseEnum(parameters, "fill", FillMode.Zero),
      Limit = parameters.GetInt("limit"),
      Offset = parameters.GetInt("offset") ?? 0,
      LimitPerBucket = parameters.GetInt("limitPerBucket")
    };
    if (parameters.Get("includeRowsWithoutBaseEvent") == "false")
      query.IncludeRowsWithoutBaseEvent = false;
    query.GroupBy.AddRange(parameters.GetAll("groupBy"));
    query.Filters.AddRange(ParseFilters(parameters));
    query.Segments.AddRange(ParseSegments(parameters));
    query.Metrics.AddRange(parameters.GetAll("metrics").Select(Metric.Parse));
    query.Having.AddRange(parameters.GetAll("having").Select(MetricFilter.Parse));
    foreach (var key in parameters.GetAll("orderBy"))
    {
      var descending = key.StartsWith('-');
      var name = descending ? key[1..] : key;
      if (name == "bucket")
        query.OrderBy.Add(Order.ByBucket(descending));
      else if (query.GroupBy.Contains(name))
        query.OrderBy.Add(Order.ByDimension(name, descending));
      else
        query.OrderBy.Add(Order.By(Metric.Parse(name), descending));
    }
    var result = await Engine(context).QueryAsync(query, cancellationToken);
    WriteResult(writer, result);
  }

  public static async Task FunnelAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var funnel = new FunnelQuery
    {
      Site = parameters.Get("site"),
      Range = ParseRange(parameters),
      TimeZone = ParseTimeZone(parameters)
    };
    var window = parameters.Get("windowMinutes");
    if (window != null)
    {
      funnel.Window = double.TryParse(window, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
          ? TimeSpan.FromMinutes(minutes)
          : throw new FormatException("'windowMinutes' must be a positive number.");
    }
    funnel.Filters.AddRange(ParseFilters(parameters));
    funnel.Segments.AddRange(ParseSegments(parameters));
    if (parameters.Element("steps") is not { ValueKind: JsonValueKind.Array } steps)
      throw new FormatException("'steps' must be an array.");
    foreach (var item in steps.EnumerateArray())
    {
      var step = new ApiParameters(new QueryCollection(), item);
      var eventName = step.Get("event") ?? throw new FormatException("A funnel step needs an 'event'.");
      funnel.Steps.Add(new FunnelStep(
          step.Get("name") ?? eventName,
          eventName,
          step.GetAll("filters").Select(Filter.Parse).ToArray()));
    }
    var result = await Engine(context).QueryFunnelAsync(funnel, cancellationToken);
    writer.WriteStartObject();
    writer.WriteString("from", result.From);
    writer.WriteString("to", result.To);
    writer.WriteNumber("conversionRate", result.ConversionRate);
    writer.WriteStartArray("steps");
    foreach (var step in result.Steps)
    {
      writer.WriteStartObject();
      writer.WriteString("name", step.Name);
      writer.WriteNumber("visitors", step.Visitors);
      writer.WriteNumber("conversionRate", step.ConversionRate);
      writer.WriteNumber("stepConversionRate", step.StepConversionRate);
      writer.WriteNumber("dropOff", step.DropOff);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();
    WritePlan(writer, result.Plan);
    writer.WriteEndObject();
  }

  #endregion

  #region Writers

  static void WriteNullable(Utf8JsonWriter writer, string name, double? value)
  {
    if (value.HasValue)
      writer.WriteNumber(name, value.Value);
    else
      writer.WriteNull(name);
  }

  static void WriteSummary(Utf8JsonWriter writer, WebSummary summary)
  {
    writer.WriteStartObject();
    writer.WriteNumber("visitors", summary.Visitors);
    writer.WriteNumber("pageViews", summary.PageViews);
    WriteNullable(writer, "sessions", summary.Sessions);
    WriteNullable(writer, "bounceRate", summary.BounceRate);
    WriteNullable(writer, "averageSessionDuration", summary.AverageSessionDuration?.TotalSeconds);
    WriteNullable(writer, "pageViewsPerSession", summary.PageViewsPerSession);
    WriteNullable(writer, "botHits", summary.BotHits);
    if (summary.Previous != null)
    {
      writer.WritePropertyName("previous");
      WriteSummary(writer, summary.Previous);
    }
    writer.WriteEndObject();
  }

  static void WriteBreakdownRow(Utf8JsonWriter writer, WebBreakdownRow row)
  {
    writer.WriteStartObject();
    writer.WriteString("value", row.Value);
    writer.WriteNumber("visitors", row.Visitors);
    writer.WriteNumber("pageViews", row.PageViews);
    writer.WriteNumber("visitorShare", row.VisitorShare);
    WriteNullable(writer, "sessions", row.Sessions);
    WriteNullable(writer, "bounceRate", row.BounceRate);
    WriteNullable(writer, "averageSessionDuration", row.AverageSessionDuration?.TotalSeconds);
    if (row.Previous != null)
    {
      writer.WritePropertyName("previous");
      WriteBreakdownRow(writer, row.Previous);
    }
    writer.WriteEndObject();
  }

  static void WritePageStats(Utf8JsonWriter writer, PageStats page)
  {
    writer.WriteStartObject();
    writer.WriteString("page", page.Page);
    writer.WriteNumber("visitors", page.Visitors);
    writer.WriteNumber("pageViews", page.PageViews);
    WriteNullable(writer, "entrances", page.Entrances);
    WriteNullable(writer, "exits", page.Exits);
    WriteNullable(writer, "exitRate", page.ExitRate);
    WriteNullable(writer, "botHits", page.BotHits);
    if (page.Previous != null)
    {
      writer.WritePropertyName("previous");
      WritePageStats(writer, page.Previous);
    }
    writer.WriteEndObject();
  }

  static void WriteBotStats(Utf8JsonWriter writer, BotStats bot)
  {
    writer.WriteStartObject();
    writer.WriteString("name", bot.Name);
    writer.WriteString("category", bot.Category);
    writer.WriteNumber("hits", bot.Hits);
    writer.WriteNumber("share", bot.Share);
    if (bot.Previous != null)
    {
      writer.WritePropertyName("previous");
      WriteBotStats(writer, bot.Previous);
    }
    writer.WriteEndObject();
  }

  static void WriteReport<T>(Utf8JsonWriter writer, WebReport<T> report, Action<Utf8JsonWriter, T> writeRow)
  {
    writer.WriteStartObject();
    writer.WriteNumber("totalRows", report.TotalRows);
    writer.WriteStartArray("rows");
    foreach (var row in report.Rows)
      writeRow(writer, row);
    writer.WriteEndArray();
    WritePlan(writer, report.Plan);
    writer.WriteEndObject();
  }

  static void WriteRow(Utf8JsonWriter writer, AnalyticsResult result, AnalyticsRow row)
  {
    writer.WriteStartObject();
    if (row.Bucket.HasValue)
      writer.WriteString("bucket", row.Bucket.Value);
    if (result.GroupBy.Count > 0)
    {
      writer.WriteStartObject("dimensions");
      for (var i = 0; i < result.GroupBy.Count; ++i)
        writer.WriteString(result.GroupBy[i], row.Dimensions[i]);
      writer.WriteEndObject();
    }
    writer.WriteStartObject("values");
    for (var i = 0; i < result.Metrics.Count; ++i)
      writer.WriteNumber(result.Metrics[i].Key, row.Values[i]);
    writer.WriteEndObject();
    if (row.Previous != null)
    {
      writer.WritePropertyName("previous");
      WriteRow(writer, result, row.Previous);
    }
    writer.WriteEndObject();
  }

  static void WriteResult(Utf8JsonWriter writer, AnalyticsResult result)
  {
    writer.WriteStartObject();
    writer.WriteString("from", result.From);
    writer.WriteString("to", result.To);
    writer.WriteString("timeZone", result.TimeZone.Id);
    writer.WriteString("interval", result.Interval.ToString());
    writer.WriteStartArray("groupBy");
    foreach (var dimension in result.GroupBy)
      writer.WriteStringValue(dimension);
    writer.WriteEndArray();
    writer.WriteStartArray("metrics");
    foreach (var metric in result.Metrics)
      writer.WriteStringValue(metric.Key);
    writer.WriteEndArray();
    writer.WriteNumber("totalRows", result.TotalRows);
    writer.WritePropertyName("totals");
    WriteRow(writer, result, result.Totals);
    writer.WriteStartArray("rows");
    foreach (var row in result.Rows)
      WriteRow(writer, result, row);
    writer.WriteEndArray();
    WritePlan(writer, result.Plan);
    writer.WriteEndObject();
  }

  static void WritePlan(Utf8JsonWriter writer, QueryPlan plan)
  {
    writer.WriteStartObject("plan");
    writer.WriteBoolean("complete", plan.IsComplete);
    writer.WriteStartArray("warnings");
    foreach (var warning in plan.Warnings)
      writer.WriteStringValue(warning);
    writer.WriteEndArray();
    writer.WriteStartArray("steps");
    foreach (var step in plan.Steps)
    {
      writer.WriteStartObject();
      writer.WriteString("event", step.Event);
      writer.WriteString("source", step.Source.ToString());
      writer.WriteString("from", step.From);
      writer.WriteString("to", step.To);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
  }

  #endregion
}
