using System.Text.Json;

sealed class DashboardRequestException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
  public int StatusCode { get; } = statusCode;
}

// JSON bodies and replies of the dashboard's own APIs, with the system store's serializer
// options (camel case, enum names).
static class DashboardJson
{
  const int MaxBodyBytes = 1024 * 1024;

  public static async Task<T> ReadAsync<T>(HttpRequest request)
  {
    if (request.ContentLength > MaxBodyBytes)
      throw new DashboardRequestException("The request is too large.", StatusCodes.Status413PayloadTooLarge);
    using var buffer = new MemoryStream();
    var chunk = new byte[16 * 1024];
    int read;
    while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
    {
      if (buffer.Length + read > MaxBodyBytes)
        throw new DashboardRequestException("The request is too large.", StatusCodes.Status413PayloadTooLarge);
      buffer.Write(chunk, 0, read);
    }
    if (buffer.Length == 0) throw new DashboardRequestException("Send a JSON body.");
    try
    {
      return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), SystemStore.Json)
          ?? throw new DashboardRequestException("Send a JSON body.");
    }
    catch (JsonException error)
    {
      throw new DashboardRequestException($"The JSON body is invalid: {error.Message}");
    }
  }

  public static IResult Ok(object value) => Results.Json(value, SystemStore.Json);

  public static IResult Error(string message, int statusCode = StatusCodes.Status400BadRequest, object extra = null) =>
      Results.Json(extra ?? new { error = message }, SystemStore.Json, statusCode: statusCode);

  // Turns the dashboard's expected failures into JSON errors and marks replies as private.
  public static RouteGroupBuilder WithDashboardErrors(this RouteGroupBuilder group)
  {
    group.AddEndpointFilter(async (context, next) =>
    {
      context.HttpContext.Response.Headers.CacheControl = "no-store";
      try
      {
        return await next(context);
      }
      catch (DashboardRequestException error) { return Error(error.Message, error.StatusCode); }
      catch (UserStoreException error) { return Error(error.Message); }
      catch (SettingsValidationException error) { return Error(error.Message, extra: new { error = error.Message, key = error.Key }); }
      catch (SettingsConfirmationRequiredException error)
      {
        return Error(error.Message, StatusCodes.Status409Conflict, new { error = error.Message, warnings = error.Warnings });
      }
    });
    return group;
  }
}
