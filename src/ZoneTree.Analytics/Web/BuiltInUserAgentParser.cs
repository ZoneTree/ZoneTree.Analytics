namespace ZoneTree.Analytics.Web;

/// <summary>
/// The built-in, dependency-free user agent parser. It covers the browsers and operating
/// systems that make up practically all web traffic. Results are cached.
/// </summary>
sealed class BuiltInUserAgentParser : IUserAgentParser
{
  readonly UserAgentCache<UserAgentInfo> cache = new(ParseCore);

  public static BuiltInUserAgentParser Instance { get; } = new();

  public UserAgentInfo Parse(string userAgent) => cache.Get(userAgent);

  static UserAgentInfo ParseCore(string ua)
  {
    var lower = ua.ToLowerInvariant();
    var (os, osVersion) = DetectOperatingSystem(ua);
    var (browser, browserVersion) = DetectBrowser(ua);
    var device = DetectDevice(lower, os);
    return new UserAgentInfo(browser, browserVersion, os, osVersion, device);
  }

  static (string, string) DetectBrowser(string ua)
  {
    if (TryVersion(ua, "EdgA/", out var v) || TryVersion(ua, "EdgiOS/", out v) ||
        TryVersion(ua, "Edg/", out v) || TryVersion(ua, "Edge/", out v))
      return ("Edge", v);
    if (TryVersion(ua, "OPR/", out v) || TryVersion(ua, "OPT/", out v) ||
        TryVersion(ua, "OPiOS/", out v) || TryVersion(ua, "Opera/", out v))
      return ("Opera", v);
    if (TryVersion(ua, "SamsungBrowser/", out v))
      return ("Samsung Internet", v);
    if (TryVersion(ua, "YaBrowser/", out v))
      return ("Yandex Browser", v);
    if (TryVersion(ua, "Vivaldi/", out v))
      return ("Vivaldi", v);
    if (TryVersion(ua, "UCBrowser/", out v))
      return ("UC Browser", v);
    if (TryVersion(ua, "DuckDuckGo/", out v) || TryVersion(ua, "Ddg/", out v))
      return ("DuckDuckGo", v);
    if (TryVersion(ua, "FxiOS/", out v) || TryVersion(ua, "Firefox/", out v))
      return ("Firefox", v);
    if (ua.Contains("FBAN/", StringComparison.Ordinal) || ua.Contains("FBAV/", StringComparison.Ordinal))
      return ("Facebook App", TryVersion(ua, "FBAV/", out v) ? v : "");
    if (TryVersion(ua, "Instagram ", out v))
      return ("Instagram App", v);
    if (TryVersion(ua, "CriOS/", out v))
      return ("Chrome", v);
    if (ua.Contains("; wv)", StringComparison.Ordinal) && TryVersion(ua, "Chrome/", out v))
      return ("Android WebView", v);
    if (TryVersion(ua, "Chrome/", out v) || TryVersion(ua, "Chromium/", out v))
      return ("Chrome", v);
    if (ua.Contains("Safari/", StringComparison.Ordinal))
    {
      if (ua.Contains("Android", StringComparison.Ordinal))
        return ("Android Browser", TryVersion(ua, "Version/", out v) ? v : "");
      return ("Safari", TryVersion(ua, "Version/", out v) ? v : "");
    }
    if (TryVersion(ua, "MSIE ", out v))
      return ("Internet Explorer", v);
    if (ua.Contains("Trident/", StringComparison.Ordinal))
      return ("Internet Explorer", TryVersion(ua, "rv:", out v) ? v : "11");
    if (ua.Contains("AppleWebKit", StringComparison.Ordinal) &&
        (ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal)))
      return ("Safari", "");
    return ("", "");
  }

  static (string, string) DetectOperatingSystem(string ua)
  {
    if (ua.Contains("Windows Phone", StringComparison.Ordinal))
      return ("Windows Phone", "");
    if (ua.Contains("Windows", StringComparison.Ordinal))
    {
      if (TryVersion(ua, "Windows NT ", out var nt, allowDots: true))
      {
        var version = nt switch
        {
          "10.0" => "10",
          "6.3" => "8.1",
          "6.2" => "8",
          "6.1" => "7",
          "6.0" => "Vista",
          "5.1" or "5.2" => "XP",
          _ => nt
        };
        return ("Windows", version);
      }
      return ("Windows", "");
    }
    if (ua.Contains("iPhone", StringComparison.Ordinal) ||
        ua.Contains("iPad", StringComparison.Ordinal) ||
        ua.Contains("iPod", StringComparison.Ordinal))
    {
      var version = TryVersion(ua, " OS ", out var v, allowDots: true, allowUnderscore: true)
          ? v.Replace('_', '.')
          : "";
      return ("iOS", MajorMinor(version));
    }
    if (ua.Contains("Android", StringComparison.Ordinal))
    {
      var version = TryVersion(ua, "Android ", out var v, allowDots: true) ? v : "";
      return ("Android", Major(version));
    }
    if (ua.Contains("CrOS", StringComparison.Ordinal))
      return ("ChromeOS", "");
    if (ua.Contains("Mac OS X", StringComparison.Ordinal) || ua.Contains("Macintosh", StringComparison.Ordinal))
    {
      var version = TryVersion(ua, "Mac OS X ", out var v, allowDots: true, allowUnderscore: true)
          ? v.Replace('_', '.')
          : "";
      return ("macOS", MajorMinor(version));
    }
    if (ua.Contains("Ubuntu", StringComparison.Ordinal))
      return ("Ubuntu", "");
    if (ua.Contains("Fedora", StringComparison.Ordinal))
      return ("Fedora", "");
    if (ua.Contains("FreeBSD", StringComparison.Ordinal))
      return ("FreeBSD", "");
    if (ua.Contains("Linux", StringComparison.Ordinal) || ua.Contains("X11", StringComparison.Ordinal))
      return ("Linux", "");
    return ("", "");
  }

  static string DetectDevice(string lower, string os)
  {
    if (lower.Contains("ipad", StringComparison.Ordinal) ||
        lower.Contains("tablet", StringComparison.Ordinal) ||
        lower.Contains("kindle", StringComparison.Ordinal) ||
        lower.Contains("silk/", StringComparison.Ordinal) ||
        lower.Contains("playbook", StringComparison.Ordinal))
      return "Tablet";
    if (os == "Android")
      return lower.Contains("mobile", StringComparison.Ordinal) ? "Mobile" : "Tablet";
    if (lower.Contains("mobi", StringComparison.Ordinal) ||
        lower.Contains("iphone", StringComparison.Ordinal) ||
        lower.Contains("ipod", StringComparison.Ordinal) ||
        os == "Windows Phone")
      return "Mobile";
    return "Desktop";
  }

  static bool TryVersion(
      string ua,
      string token,
      out string version,
      bool allowDots = false,
      bool allowUnderscore = false)
  {
    version = "";
    var index = ua.IndexOf(token, StringComparison.Ordinal);
    if (index < 0)
      return false;
    var start = index + token.Length;
    var end = start;
    while (end < ua.Length)
    {
      var c = ua[end];
      if (char.IsAsciiDigit(c) || (allowDots && c == '.') || (allowUnderscore && c == '_'))
      {
        ++end;
        continue;
      }
      break;
    }
    if (end == start)
      return false;
    var raw = ua[start..end];
    version = allowDots || allowUnderscore ? raw.TrimEnd('.', '_') : raw;
    return true;
  }

  static string Major(string version)
  {
    var dot = version.IndexOf('.');
    return dot < 0 ? version : version[..dot];
  }

  static string MajorMinor(string version)
  {
    var first = version.IndexOf('.');
    if (first < 0)
      return version;
    var second = version.IndexOf('.', first + 1);
    return second < 0 ? version : version[..second];
  }
}
