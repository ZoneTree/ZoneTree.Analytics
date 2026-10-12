using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ZoneTree.Analytics.AspNetCore;

static class JsonRequestBody
{
  public static async Task<JsonDocument> ReadAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
  {
    if (request.ContentLength > limit)
      throw TooLarge();
    using var memory = new MemoryStream();
    var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(limit, 4096));
    try
    {
      while (true)
      {
        // Read one extra byte to detect an oversized body, even with a missing or incorrect
        // Content-Length. Never buffer bytes beyond the configured limit.
        var size = (int)Math.Min(buffer.Length, limit - memory.Length + 1);
        var read = await request.Body.ReadAsync(buffer.AsMemory(0, size), cancellationToken);
        if (read == 0)
          break;
        if (memory.Length + read > limit)
          throw TooLarge();
        memory.Write(buffer, 0, read);
      }
      var json = memory.GetBuffer().AsMemory(0, (int)memory.Length);
      if (json.Span.StartsWith("\uFEFF"u8))
        json = json[3..];
      var document = JsonDocument.Parse(json);
      try
      {
        // JsonDocument defers decoding strings and property names. Validate the entire input
        // here, including unused fields, without catching exceptions from application code.
        ValidateUnicode(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
          throw new JsonException("The JSON body must be an object.");
        return document;
      }
      catch (InvalidOperationException exception)
      {
        document.Dispose();
        throw new JsonException("The JSON body contains invalid Unicode.", exception);
      }
      catch
      {
        document.Dispose();
        throw;
      }
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  static BadHttpRequestException TooLarge() => new("The JSON body exceeds the configured payload limit.", StatusCodes.Status413PayloadTooLarge);

  static void ValidateUnicode(JsonElement element)
  {
    switch (element.ValueKind)
    {
      case JsonValueKind.String:
        _ = element.GetString();
        break;
      case JsonValueKind.Object:
        foreach (var property in element.EnumerateObject())
        {
          _ = property.Name;
          ValidateUnicode(property.Value);
        }
        break;
      case JsonValueKind.Array:
        foreach (var item in element.EnumerateArray())
          ValidateUnicode(item);
        break;
    }
  }
}
