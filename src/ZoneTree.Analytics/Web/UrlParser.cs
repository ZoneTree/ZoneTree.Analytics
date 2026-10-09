namespace ZoneTree.Analytics.Web;

/// <summary>
/// The parts of a page URL used for analytics.
/// </summary>
sealed class ParsedUrl
{
  public string Hostname = "";

  public string Path = "";

  public string UtmSource;

  public string UtmMedium;

  public string UtmCampaign;

  public string UtmTerm;

  public string UtmContent;
}

/// <summary>
/// Parses page URLs into hostname, normalized path and UTM parameters.
/// </summary>
static class UrlParser
{
  public static ParsedUrl Parse(string url, WebOptions web, bool includeQueryString)
  {
    var result = new ParsedUrl();
    if (string.IsNullOrWhiteSpace(url))
      return result;
    url = url.Trim();

    string path;
    string query = null;
    if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))
    {
      path = url;
    }
    else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
             (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
    {
      result.Hostname = NormalizeHost(uri.Host);
      path = uri.AbsolutePath + uri.Query;
    }
    else if (Uri.TryCreate("http://" + url.TrimStart('/'), UriKind.Absolute, out uri))
    {
      result.Hostname = NormalizeHost(uri.Host);
      path = uri.AbsolutePath + uri.Query;
    }
    else
    {
      path = "/" + url;
    }

    var hashIndex = path.IndexOf('#');
    if (hashIndex >= 0)
      path = path[..hashIndex];
    var queryIndex = path.IndexOf('?');
    if (queryIndex >= 0)
    {
      query = path[(queryIndex + 1)..];
      path = path[..queryIndex];
    }

    string keptQuery = null;
    if (!string.IsNullOrEmpty(query))
      keptQuery = ParseQuery(query, result, includeQueryString);

    if (path.Length == 0)
      path = "/";
    if (web.TrimTrailingSlash && path.Length > 1 && path[^1] == '/')
      path = path.TrimEnd('/');
    if (path.Length == 0)
      path = "/";
    if (web.LowercasePaths)
      path = path.ToLowerInvariant();
    if (!string.IsNullOrEmpty(keptQuery))
      path = path + "?" + keptQuery;
    result.Path = path;
    return result;
  }

  static string ParseQuery(string query, ParsedUrl result, bool keepQuery)
  {
    List<string> kept = keepQuery ? [] : null;
    string refParam = null;
    foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
      var eq = part.IndexOf('=');
      var key = eq < 0 ? part : part[..eq];
      var value = eq < 0 ? "" : Decode(part[(eq + 1)..]);
      switch (key.ToLowerInvariant())
      {
        case "utm_source":
          result.UtmSource = value;
          continue;
        case "utm_medium":
          result.UtmMedium = value;
          continue;
        case "utm_campaign":
          result.UtmCampaign = value;
          continue;
        case "utm_term":
          result.UtmTerm = value;
          continue;
        case "utm_content":
          result.UtmContent = value;
          continue;
        case "ref":
        case "source":
          refParam ??= value;
          break;
      }
      kept?.Add(part);
    }
    if (string.IsNullOrWhiteSpace(result.UtmSource) && !string.IsNullOrWhiteSpace(refParam))
      result.UtmSource = refParam;
    return kept == null || kept.Count == 0 ? null : string.Join('&', kept);
  }

  static string Decode(string value)
  {
    try
    {
      return Uri.UnescapeDataString(value.Replace('+', ' ')).Trim();
    }
    catch (UriFormatException)
    {
      return value;
    }
  }

  public static string NormalizeHost(string host)
  {
    if (string.IsNullOrEmpty(host))
      return "";
    host = host.Trim().TrimEnd('.').ToLowerInvariant();
    if (host.StartsWith("www.", StringComparison.Ordinal))
      host = host[4..];
    return host;
  }

  /// <summary>
  /// Extracts the normalized host of an absolute URL, or empty.
  /// </summary>
  public static string GetHost(string url)
  {
    if (string.IsNullOrWhiteSpace(url))
      return "";
    url = url.Trim();
    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
      return NormalizeHost(uri.Host);
    if (!url.Contains("://", StringComparison.Ordinal) &&
        Uri.TryCreate("http://" + url, UriKind.Absolute, out uri) &&
        uri.Host.Contains('.'))
      return NormalizeHost(uri.Host);
    return "";
  }
}
