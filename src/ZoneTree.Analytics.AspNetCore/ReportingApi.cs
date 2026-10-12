using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
    var json = ReportingJsonContext.Default;
    group.MapGet("/summary", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.SummaryAsync, json.SummaryResponse)));
    group.MapGet("/timeseries", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.TimeseriesAsync, json.TimeseriesResponse)));
    group.MapGet("/breakdown", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.BreakdownAsync, json.BreakdownResponse)));
    group.MapGet("/pages", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.PagesAsync, json.PagesResponse)));
    group.MapGet("/exit-pages", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.ExitPagesAsync, json.PagesResponse)));
    group.MapGet("/goals", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.GoalsAsync, json.GoalsResponse)));
    group.MapGet("/bots", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.BotsAsync, json.BotsResponse)));
    group.MapGet("/realtime", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.RealtimeAsync, json.RealtimeResponse)));
    group.MapPost("/query", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.QueryAsync, json.QueryResponse)));
    group.MapPost("/funnel", (RequestDelegate)(context => ReportingApi.Run(context, ReportingApi.FunnelAsync, json.FunnelResponse)));
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
      return query[name].Where(x => !string.IsNullOrEmpty(x))
          .SelectMany(x => SplitList(x, name is "filter" or "filters")).ToList();
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

  // Commas inside a filter group or metric formula belong to that expression.
  static IEnumerable<string> SplitList(string text, bool filters)
  {
    var start = 0;
    var depth = 0;
    var quoted = false;
    for (var i = 0; i < text.Length; ++i)
    {
      var c = text[i];
      if (quoted && c == '\\' && i + 1 < text.Length)
      {
        ++i;
        continue;
      }
      if (c == '"' && depth > 0)
        quoted = !quoted;
      if (quoted)
        continue;
      // In a single filter, parentheses can be literal values or escaped regex tokens.
      // Only a leading any(...) or all(...) introduces a filter group's parentheses.
      if (c == '(' && (!filters || depth > 0 || text[start..i].Trim() is "any" or "all"))
        ++depth;
      else if (c == ')' && depth > 0)
        --depth;
      else if (c == ',' && depth == 0)
      {
        yield return text[start..i];
        start = i + 1;
      }
    }
    yield return text[start..];
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
  public delegate Task<T> Handler<T>(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken);

  public static async Task Run<T>(HttpContext context, Handler<T> handler, JsonTypeInfo<T> jsonTypeInfo)
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
      var response = await handler(context, parameters, cancellationToken);
      await WriteResponse(context, StatusCodes.Status200OK, response, jsonTypeInfo);
    }
    catch (AnalyticsQueryTimeoutException e) when (!context.Response.HasStarted)
    {
      await WriteError(context, StatusCodes.Status503ServiceUnavailable, e.Message);
    }
    catch (Exception e) when (!context.Response.HasStarted &&
        e is AnalyticsQueryException or FormatException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
    {
      await WriteError(context, StatusCodes.Status400BadRequest, e.Message);
    }
    finally
    {
      document?.Dispose();
    }
  }

  static Task WriteError(HttpContext context, int status, string message) =>
      WriteResponse(context, status, new ErrorResponse(message), ReportingJsonContext.Default.ErrorResponse);

  static async Task WriteResponse<T>(HttpContext context, int status, T response, JsonTypeInfo<T> jsonTypeInfo)
  {
    // Finish serialization before committing headers so a failed report can still return
    // a complete error response. Explicit generated metadata keeps this path AOT compatible.
    using var buffer = new MemoryStream();
    await JsonSerializer.SerializeAsync(buffer, response, jsonTypeInfo, context.RequestAborted);
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store";
    buffer.Position = 0;
    await buffer.CopyToAsync(context.Response.Body, context.RequestAborted);
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

  public static async Task<SummaryResponse> SummaryAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken)
  {
    var report = await Engine(context).Web.GetSummaryAsync(Scope.Parse(parameters).ToRequest(), cancellationToken);
    return new ReportingResponseMapper().Summary(report);
  }

  public static async Task<ReportResponse<TimeseriesRow>> TimeseriesAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken)
  {
    var interval = ParseInterval(parameters, TimeInterval.Day);
    var report = await Engine(context).Web.GetTimeseriesAsync(Scope.Parse(parameters).ToRequest(), interval, cancellationToken);
    return new ReportingResponseMapper().Timeseries(report);
  }

  public static async Task<ReportResponse<BreakdownRow>> BreakdownAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken)
  {
    var dimension = parameters.Get("dimension") ?? throw new FormatException("'dimension' is required.");
    var report = await Engine(context).Web.GetBreakdownAsync(Scope.Parse(parameters).ToRequest(), dimension, cancellationToken);
    return new ReportingResponseMapper().Breakdown(report);
  }

  public static Task<ReportResponse<PageRow>> PagesAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken) =>
      GetPagesAsync(context, parameters, false, cancellationToken);

  public static Task<ReportResponse<PageRow>> ExitPagesAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken) =>
      GetPagesAsync(context, parameters, true, cancellationToken);

  static async Task<ReportResponse<PageRow>> GetPagesAsync(HttpContext context, ApiParameters parameters, bool exits, CancellationToken cancellationToken)
  {
    var request = Scope.Parse(parameters).ToRequest();
    var web = Engine(context).Web;
    var report = exits
        ? await web.GetExitPagesAsync(request, cancellationToken)
        : await web.GetPagesAsync(request, cancellationToken);
    return new ReportingResponseMapper().Pages(report);
  }

  public static async Task<ReportResponse<GoalRow>> GoalsAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken)
  {
    var report = await Engine(context).Web.GetGoalsAsync(Scope.Parse(parameters).ToRequest(), cancellationToken);
    return new ReportingResponseMapper().Goals(report);
  }

  public static async Task<ReportResponse<BotRow>> BotsAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken)
  {
    var report = await Engine(context).Web.GetBotsAsync(Scope.Parse(parameters).ToRequest(), cancellationToken);
    return new ReportingResponseMapper().Bots(report);
  }

  public static async Task<RealtimeResponse> RealtimeAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken)
  {
    var minutes = parameters.GetInt("minutes") ?? 5;
    var visitors = await Engine(context).Web.GetCurrentVisitorsAsync(
        parameters.Get("site"), TimeSpan.FromMinutes(Math.Max(1, minutes)), cancellationToken);
    return new(visitors, Math.Max(1, minutes));
  }

  public static async Task<QueryResponse> QueryAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken)
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
    return new ReportingResponseMapper().Query(result);
  }

  public static async Task<FunnelResponse> FunnelAsync(HttpContext context, ApiParameters parameters, CancellationToken cancellationToken)
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
    return new ReportingResponseMapper().Funnel(result);
  }

  #endregion
}
