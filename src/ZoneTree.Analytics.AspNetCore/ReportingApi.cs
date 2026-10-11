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
      var site = Engine(context).NormalizeSite(parameters.Get("site"));
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
    catch (AnalyticsQueryTimeoutException e)
    {
      await WriteError(context, StatusCodes.Status503ServiceUnavailable, e.Message);
    }
    catch (Exception e) when (e is AnalyticsQueryException or FormatException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
    {
      await WriteError(context, StatusCodes.Status400BadRequest, e.Message);
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

  /// <summary>
  /// The parameters every report and query shares. A parameter that is not given keeps the
  /// default of the request type.
  /// </summary>
  sealed class Scope
  {
    public string Site;

    public DateRange? Range;

    public TimeZoneInfo TimeZone;

    public ComparisonPeriod Compare;

    public int? Limit;

    public int? Offset;

    public Filter[] Filters;

    public Segment[] Segments;

    public static Scope Parse(ApiParameters parameters) => new()
    {
      Site = parameters.Get("site"),
      Range = ParseRange(parameters),
      TimeZone = ParseTimeZone(parameters),
      Compare = ParseComparison(parameters),
      Limit = parameters.GetInt("limit"),
      Offset = parameters.GetInt("offset"),
      Filters = [.. parameters.GetAll("filter").Concat(parameters.GetAll("filters")).Select(Filter.Parse)],
      Segments = [.. ParseSegments(parameters)]
    };

    public WebReportRequest ToRequest()
    {
      var request = new WebReportRequest { Site = Site, TimeZone = TimeZone, Compare = Compare };
      if (Range is { } range)
        request.Range = range;
      if (Limit is { } limit)
        request.Limit = limit;
      if (Offset is { } offset)
        request.Offset = offset;
      request.Filters.AddRange(Filters);
      request.Segments.AddRange(Segments);
      return request;
    }

    public AnalyticsQuery ToQuery()
    {
      var query = new AnalyticsQuery { Site = Site, TimeZone = TimeZone, Compare = Compare, Limit = Limit };
      if (Range is { } range)
        query.Range = range;
      if (Offset is { } offset)
        query.Offset = offset;
      query.Filters.AddRange(Filters);
      query.Segments.AddRange(Segments);
      return query;
    }

    public FunnelQuery ToFunnel()
    {
      if (Compare != null || Limit != null || Offset != null)
        throw new FormatException("Funnels take no 'compare', 'limit' or 'offset'.");
      var funnel = new FunnelQuery { Site = Site, TimeZone = TimeZone };
      if (Range is { } range)
        funnel.Range = range;
      funnel.Filters.AddRange(Filters);
      funnel.Segments.AddRange(Segments);
      return funnel;
    }
  }

  static TimeZoneInfo ParseTimeZone(ApiParameters parameters)
  {
    var id = parameters.Get("tz");
    return string.IsNullOrWhiteSpace(id) ? null : TimeZoneInfo.FindSystemTimeZoneById(id);
  }

  /// <summary>
  /// A range from "range" (see <see cref="DateRange.Parse"/>) or from "from" and "to", which are
  /// both days (yyyy-MM-dd) or both date-times; null when none is given.
  /// </summary>
  static DateRange? ParseRange(ApiParameters parameters)
  {
    var from = parameters.Get("from");
    var to = parameters.Get("to");
    if (from != null || to != null)
    {
      if (from == null || to == null)
        throw new FormatException("'from' and 'to' must be given together.");
      return DateRange.Parse(from + ".." + to);
    }
    var range = parameters.Get("range");
    return string.IsNullOrWhiteSpace(range) ? null : DateRange.Parse(range);
  }

  static ComparisonPeriod ParseComparison(ApiParameters parameters)
  {
    var compare = parameters.Get("compare");
    return string.IsNullOrWhiteSpace(compare) || compare.Trim() == "none" ? null : ComparisonPeriod.Parse(compare);
  }

  static TEnum? ParseEnum<TEnum>(ApiParameters parameters, string name) where TEnum : struct, Enum
  {
    var value = parameters.Get(name);
    if (string.IsNullOrWhiteSpace(value))
      return null;
    return Enum.TryParse<TEnum>(value.Replace("-", "").Replace("_", ""), ignoreCase: true, out var result) && Enum.IsDefined(result)
        ? result
        : throw new FormatException($"Unknown {name} '{value}'.");
  }

  static TimeInterval ParseInterval(ApiParameters parameters, TimeInterval fallback)
  {
    var value = parameters.Get("interval");
    return string.IsNullOrWhiteSpace(value) ? fallback : TimeInterval.Parse(value);
  }

  /// <summary>
  /// Segments as text (see <see cref="Segment.Parse"/>) or as objects with "event", "filters",
  /// "scope" and "exclude".
  /// </summary>
  static IEnumerable<Segment> ParseSegments(ApiParameters parameters)
  {
    if (parameters.Element("segments") is not { } element)
    {
      foreach (var text in parameters.GetAll("segment"))
        yield return Segment.Parse(text);
      yield break;
    }
    if (element.ValueKind != JsonValueKind.Array)
      throw new FormatException("'segments' must be an array.");
    foreach (var item in element.EnumerateArray())
    {
      if (item.ValueKind == JsonValueKind.String)
      {
        yield return Segment.Parse(item.GetString());
        continue;
      }
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

  #endregion

  #region Handlers

  public static async Task SummaryAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var report = await Engine(context).Web.GetSummaryAsync(Scope.Parse(parameters).ToRequest(), cancellationToken);
    writer.WriteStartObject();
    WriteRange(writer, report.From, report.To);
    writer.WritePropertyName("summary");
    var nonFinite = WriteSummary(writer, report.Summary);
    WritePlan(writer, report.Plan, nonFinite);
    writer.WriteEndObject();
  }

  public static async Task TimeseriesAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var interval = ParseInterval(parameters, TimeInterval.Day);
    var report = await Engine(context).Web.GetTimeseriesAsync(Scope.Parse(parameters).ToRequest(), interval, cancellationToken);
    WriteReport(writer, report, (w, point) =>
    {
      w.WriteStartObject();
      w.WriteString("bucket", point.Bucket);
      w.WritePropertyName("summary");
      var nonFinite = WriteSummary(w, point.Summary);
      w.WriteEndObject();
      return nonFinite;
    });
  }

  public static async Task BreakdownAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var dimension = parameters.Get("dimension") ?? throw new FormatException("'dimension' is required.");
    var report = await Engine(context).Web.GetBreakdownAsync(Scope.Parse(parameters).ToRequest(), dimension, cancellationToken);
    WriteReport(writer, report, WriteBreakdownRow);
  }

  public static async Task PagesAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, bool exits, CancellationToken cancellationToken)
  {
    var request = Scope.Parse(parameters).ToRequest();
    var web = Engine(context).Web;
    var report = exits
        ? await web.GetExitPagesAsync(request, cancellationToken)
        : await web.GetPagesAsync(request, cancellationToken);
    WriteReport(writer, report, WritePageStats);
  }

  public static async Task GoalsAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var report = await Engine(context).Web.GetGoalsAsync(Scope.Parse(parameters).ToRequest(), cancellationToken);
    WriteReport(writer, report, (w, goal) =>
    {
      w.WriteStartObject();
      w.WriteString("name", goal.Name);
      w.WriteNumber("visitors", goal.Visitors);
      w.WriteNumber("events", goal.Events);
      var nonFinite = WriteNullable(w, "conversionRate", goal.ConversionRate);
      w.WriteEndObject();
      return nonFinite;
    });
  }

  public static async Task BotsAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var report = await Engine(context).Web.GetBotsAsync(Scope.Parse(parameters).ToRequest(), cancellationToken);
    WriteReport(writer, report, WriteBotStats);
  }

  public static async Task RealtimeAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var minutes = parameters.GetInt("minutes") ?? 5;
    var visitors = await Engine(context).Web.GetCurrentVisitorsAsync(
        parameters.Get("site"), TimeSpan.FromMinutes(Math.Max(1, minutes)), cancellationToken);
    writer.WriteStartObject();
    if (visitors.HasValue)
      writer.WriteNumber("visitors", visitors.Value);
    else
      writer.WriteNull("visitors");
    writer.WriteNumber("minutes", Math.Max(1, minutes));
    writer.WriteEndObject();
  }

  public static async Task QueryAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var query = Scope.Parse(parameters).ToQuery();
    query.Event = parameters.Get("event") ?? StandardEvents.PageView;
    query.Interval = ParseInterval(parameters, TimeInterval.None);
    query.LimitPerBucket = parameters.GetInt("limitPerBucket");
    if (ParseEnum<QuerySource>(parameters, "source") is { } source)
      query.Source = source;
    if (ParseEnum<FillMode>(parameters, "fill") is { } fill)
      query.Fill = fill;
    if (parameters.Get("onlyRowsWithBaseEvent") == "true")
      query.OnlyRowsWithBaseEvent = true;
    query.GroupBy.AddRange(parameters.GetAll("groupBy"));
    query.Metrics.AddRange(parameters.GetAll("metrics").Select(Metric.Parse));
    query.Having.AddRange(parameters.GetAll("having").Select(MetricFilter.Parse));
    query.OrderBy.AddRange(parameters.GetAll("orderBy").Select(key => Order.Parse(key, query.GroupBy)));
    var result = await Engine(context).QueryAsync(query, cancellationToken);
    WriteResult(writer, result);
  }

  public static async Task FunnelAsync(HttpContext context, ApiParameters parameters, Utf8JsonWriter writer, CancellationToken cancellationToken)
  {
    var funnel = Scope.Parse(parameters).ToFunnel();
    var window = parameters.Get("windowMinutes");
    if (window != null)
    {
      funnel.Window = double.TryParse(window, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
          ? TimeSpan.FromMinutes(minutes)
          : throw new FormatException("'windowMinutes' must be a positive number.");
    }
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
    var result = await Engine(context).QueryAsync(funnel, cancellationToken);
    writer.WriteStartObject();
    WriteRange(writer, result.From, result.To);
    var nonFinite = WriteNullable(writer, "conversionRate", result.ConversionRate);
    writer.WriteStartArray("steps");
    foreach (var step in result.Steps)
    {
      writer.WriteStartObject();
      writer.WriteString("name", step.Name);
      writer.WriteNumber("visitors", step.Visitors);
      nonFinite |= WriteNullable(writer, "conversionRate", step.ConversionRate);
      nonFinite |= WriteNullable(writer, "stepConversionRate", step.StepConversionRate);
      nonFinite |= WriteNullable(writer, "dropOff", step.DropOff);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();
    WritePlan(writer, result.Plan, nonFinite);
    writer.WriteEndObject();
  }

  #endregion

  #region Writers

  static bool WriteNullable(Utf8JsonWriter writer, string name, double? value)
  {
    if (value.HasValue && double.IsFinite(value.Value))
      writer.WriteNumber(name, value.Value);
    else
      writer.WriteNull(name);
    return value.HasValue && !double.IsFinite(value.Value);
  }

  static bool WriteSummary(Utf8JsonWriter writer, WebSummary summary)
  {
    writer.WriteStartObject();
    writer.WriteNumber("visitors", summary.Visitors);
    writer.WriteNumber("pageViews", summary.PageViews);
    var nonFinite = WriteNullable(writer, "sessions", summary.Sessions);
    nonFinite |= WriteNullable(writer, "bounceRate", summary.BounceRate);
    nonFinite |= WriteNullable(writer, "averageSessionDuration", summary.AverageSessionDuration?.TotalSeconds);
    nonFinite |= WriteNullable(writer, "pageViewsPerSession", summary.PageViewsPerSession);
    nonFinite |= WriteNullable(writer, "botHits", summary.BotHits);
    if (summary.Previous != null)
    {
      writer.WritePropertyName("previous");
      nonFinite |= WriteSummary(writer, summary.Previous);
    }
    writer.WriteEndObject();
    return nonFinite;
  }

  static bool WriteBreakdownRow(Utf8JsonWriter writer, WebBreakdownRow row)
  {
    writer.WriteStartObject();
    writer.WriteString("value", row.Value);
    writer.WriteNumber("visitors", row.Visitors);
    writer.WriteNumber("pageViews", row.PageViews);
    var nonFinite = WriteNullable(writer, "visitorShare", row.VisitorShare);
    nonFinite |= WriteNullable(writer, "sessions", row.Sessions);
    nonFinite |= WriteNullable(writer, "bounceRate", row.BounceRate);
    nonFinite |= WriteNullable(writer, "averageSessionDuration", row.AverageSessionDuration?.TotalSeconds);
    if (row.Previous != null)
    {
      writer.WritePropertyName("previous");
      nonFinite |= WriteBreakdownRow(writer, row.Previous);
    }
    writer.WriteEndObject();
    return nonFinite;
  }

  static bool WritePageStats(Utf8JsonWriter writer, PageStats page)
  {
    writer.WriteStartObject();
    writer.WriteString("page", page.Page);
    writer.WriteNumber("visitors", page.Visitors);
    writer.WriteNumber("pageViews", page.PageViews);
    var nonFinite = WriteNullable(writer, "entrances", page.Entrances);
    nonFinite |= WriteNullable(writer, "exits", page.Exits);
    nonFinite |= WriteNullable(writer, "exitRate", page.ExitRate);
    nonFinite |= WriteNullable(writer, "botHits", page.BotHits);
    if (page.Previous != null)
    {
      writer.WritePropertyName("previous");
      nonFinite |= WritePageStats(writer, page.Previous);
    }
    writer.WriteEndObject();
    return nonFinite;
  }

  static bool WriteBotStats(Utf8JsonWriter writer, BotStats bot)
  {
    writer.WriteStartObject();
    writer.WriteString("name", bot.Name);
    writer.WriteString("category", bot.Category);
    writer.WriteNumber("hits", bot.Hits);
    var nonFinite = WriteNullable(writer, "share", bot.Share);
    if (bot.Previous != null)
    {
      writer.WritePropertyName("previous");
      nonFinite |= WriteBotStats(writer, bot.Previous);
    }
    writer.WriteEndObject();
    return nonFinite;
  }

  static void WriteRange(Utf8JsonWriter writer, DateTimeOffset from, DateTimeOffset to)
  {
    writer.WriteString("from", from);
    writer.WriteString("to", to);
  }

  static void WriteReport<T>(Utf8JsonWriter writer, WebReport<T> report, Func<Utf8JsonWriter, T, bool> writeRow)
  {
    writer.WriteStartObject();
    WriteRange(writer, report.From, report.To);
    writer.WriteNumber("totalRows", report.TotalRows);
    writer.WriteStartArray("rows");
    var nonFinite = false;
    foreach (var row in report.Rows)
      nonFinite |= writeRow(writer, row);
    writer.WriteEndArray();
    WritePlan(writer, report.Plan, nonFinite);
    writer.WriteEndObject();
  }

  static bool WriteRow(Utf8JsonWriter writer, AnalyticsResult result, AnalyticsRow row)
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
    var nonFinite = false;
    for (var i = 0; i < result.Metrics.Count; ++i)
      nonFinite |= WriteNullable(writer, result.Metrics[i].Key, row.Values[i]);
    writer.WriteEndObject();
    if (row.Previous != null)
    {
      writer.WritePropertyName("previous");
      nonFinite |= WriteRow(writer, result, row.Previous);
    }
    writer.WriteEndObject();
    return nonFinite;
  }

  static void WriteResult(Utf8JsonWriter writer, AnalyticsResult result)
  {
    writer.WriteStartObject();
    WriteRange(writer, result.From, result.To);
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
    var nonFinite = WriteRow(writer, result, result.Totals);
    writer.WriteStartArray("rows");
    foreach (var row in result.Rows)
      nonFinite |= WriteRow(writer, result, row);
    writer.WriteEndArray();
    WritePlan(writer, result.Plan, nonFinite);
    writer.WriteEndObject();
  }

  static void WritePlan(Utf8JsonWriter writer, QueryPlan plan, bool nonFinite = false)
  {
    writer.WriteStartObject("plan");
    writer.WriteBoolean("complete", plan.IsComplete && !nonFinite);
    writer.WriteStartArray("warnings");
    foreach (var warning in plan.Warnings)
      writer.WriteStringValue(warning);
    if (nonFinite)
      writer.WriteStringValue("Non-finite metric values (overflow or undefined arithmetic) are represented as null in this JSON response.");
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
