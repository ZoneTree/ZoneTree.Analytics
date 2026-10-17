using System.Text.RegularExpressions;

namespace ZoneTree.Analytics.Web;

/// <summary>
/// The built-in bot detector. It recognizes self-identifying bots by their user agent:
/// search engines, AI crawlers, SEO tools, link preview services, uptime monitors, browser
/// automation and HTTP libraries. Results are cached.
/// </summary>
/// <remarks>
/// Bots that send a regular browser user agent cannot be recognized this way; add another
/// <see cref="IBotDetector"/>, for example one based on IP ranges, to catch them.
/// </remarks>
sealed partial class UserAgentBotDetector : IBotDetector
{
  readonly UserAgentCache<BotInfo> cache = new(DetectCore);

  public static UserAgentBotDetector Instance { get; } = new();

  // Lowercase token, name, category. More specific tokens come first.
  static readonly (string Token, string Name, string Category)[] KnownBots =
  [
    // AI crawlers and assistants
    ("gptbot", "GPTBot", BotCategories.AI),
    ("chatgpt-user", "ChatGPT-User", BotCategories.AI),
    ("oai-searchbot", "OAI-SearchBot", BotCategories.AI),
    ("claudebot", "ClaudeBot", BotCategories.AI),
    ("claude-user", "Claude-User", BotCategories.AI),
    ("claude-searchbot", "Claude-SearchBot", BotCategories.AI),
    ("claude-web", "Claude-Web", BotCategories.AI),
    ("anthropic-ai", "anthropic-ai", BotCategories.AI),
    ("perplexitybot", "PerplexityBot", BotCategories.AI),
    ("perplexity-user", "Perplexity-User", BotCategories.AI),
    ("bytespider", "Bytespider", BotCategories.AI),
    ("ccbot", "CCBot", BotCategories.AI),
    ("amazonbot", "Amazonbot", BotCategories.AI),
    ("applebot-extended", "Applebot-Extended", BotCategories.AI),
    ("meta-externalagent", "Meta-ExternalAgent", BotCategories.AI),
    ("cohere-ai", "cohere-ai", BotCategories.AI),
    ("diffbot", "Diffbot", BotCategories.AI),
    ("youbot", "YouBot", BotCategories.AI),
    ("timpibot", "Timpibot", BotCategories.AI),
    // Search engines
    ("googlebot", "Googlebot", BotCategories.Search),
    ("google-inspectiontool", "Google-InspectionTool", BotCategories.Search),
    ("googleother", "GoogleOther", BotCategories.Search),
    ("adsbot-google", "AdsBot-Google", BotCategories.Search),
    ("mediapartners-google", "Mediapartners-Google", BotCategories.Search),
    ("feedfetcher-google", "Feedfetcher-Google", BotCategories.Search),
    ("storebot-google", "Storebot-Google", BotCategories.Search),
    ("bingbot", "Bingbot", BotCategories.Search),
    ("bingpreview", "BingPreview", BotCategories.Search),
    ("adidxbot", "AdIdxBot", BotCategories.Search),
    ("msnbot", "msnbot", BotCategories.Search),
    ("yandexbot", "YandexBot", BotCategories.Search),
    ("yandex.com/bots", "YandexBot", BotCategories.Search),
    ("baiduspider", "Baiduspider", BotCategories.Search),
    ("duckduckbot", "DuckDuckBot", BotCategories.Search),
    ("duckassistbot", "DuckAssistBot", BotCategories.AI),
    ("applebot", "Applebot", BotCategories.Search),
    ("yahoo! slurp", "Yahoo! Slurp", BotCategories.Search),
    ("sogou web spider", "Sogou", BotCategories.Search),
    ("seznambot", "SeznamBot", BotCategories.Search),
    ("yeti/", "Yeti", BotCategories.Search),
    ("petalbot", "PetalBot", BotCategories.Search),
    ("qwantbot", "Qwantbot", BotCategories.Search),
    ("qwantify", "Qwantify", BotCategories.Search),
    ("coccocbot", "coccocbot", BotCategories.Search),
    ("mojeekbot", "MojeekBot", BotCategories.Search),
    ("exabot", "Exabot", BotCategories.Search),
    // SEO and marketing tools
    ("ahrefsbot", "AhrefsBot", BotCategories.Seo),
    ("ahrefssiteaudit", "AhrefsSiteAudit", BotCategories.Seo),
    ("semrushbot", "SemrushBot", BotCategories.Seo),
    ("mj12bot", "MJ12bot", BotCategories.Seo),
    ("dotbot", "DotBot", BotCategories.Seo),
    ("rogerbot", "rogerbot", BotCategories.Seo),
    ("screaming frog", "Screaming Frog", BotCategories.Seo),
    ("serpstatbot", "serpstatbot", BotCategories.Seo),
    ("blexbot", "BLEXBot", BotCategories.Seo),
    ("dataforseobot", "DataForSeoBot", BotCategories.Seo),
    ("seokicks", "SEOkicks", BotCategories.Seo),
    ("seekport", "Seekport", BotCategories.Seo),
    ("barkrowler", "Barkrowler", BotCategories.Seo),
    // Link previews and social networks
    ("facebookexternalhit", "facebookexternalhit", BotCategories.SocialPreview),
    ("facebookcatalog", "facebookcatalog", BotCategories.SocialPreview),
    ("twitterbot", "Twitterbot", BotCategories.SocialPreview),
    ("linkedinbot", "LinkedInBot", BotCategories.SocialPreview),
    ("slackbot", "Slackbot", BotCategories.SocialPreview),
    ("discordbot", "Discordbot", BotCategories.SocialPreview),
    ("telegrambot", "TelegramBot", BotCategories.SocialPreview),
    ("whatsapp", "WhatsApp", BotCategories.SocialPreview),
    ("pinterestbot", "Pinterestbot", BotCategories.SocialPreview),
    ("redditbot", "redditbot", BotCategories.SocialPreview),
    ("embedly", "Embedly", BotCategories.SocialPreview),
    ("iframely", "Iframely", BotCategories.SocialPreview),
    ("skypeuripreview", "SkypeUriPreview", BotCategories.SocialPreview),
    ("vkshare", "vkShare", BotCategories.SocialPreview),
    ("mastodon", "Mastodon", BotCategories.SocialPreview),
    ("bluesky", "Bluesky", BotCategories.SocialPreview),
    // Monitoring and performance
    ("pingdom", "Pingdom", BotCategories.Monitoring),
    ("uptimerobot", "UptimeRobot", BotCategories.Monitoring),
    ("uptime-kuma", "Uptime Kuma", BotCategories.Monitoring),
    ("statuscake", "StatusCake", BotCategories.Monitoring),
    ("site24x7", "Site24x7", BotCategories.Monitoring),
    ("newrelicpinger", "NewRelicPinger", BotCategories.Monitoring),
    ("datadog", "Datadog", BotCategories.Monitoring),
    ("betteruptime", "Better Uptime", BotCategories.Monitoring),
    ("checkly", "Checkly", BotCategories.Monitoring),
    ("gtmetrix", "GTmetrix", BotCategories.Monitoring),
    ("lighthouse", "Lighthouse", BotCategories.Monitoring),
    ("pagespeed", "PageSpeed Insights", BotCategories.Monitoring),
    ("vercel-screenshot", "Vercel", BotCategories.Monitoring),
    // Browser automation
    ("headlesschrome", "HeadlessChrome", BotCategories.Automation),
    ("phantomjs", "PhantomJS", BotCategories.Automation),
    ("puppeteer", "Puppeteer", BotCategories.Automation),
    ("playwright", "Playwright", BotCategories.Automation),
    ("selenium", "Selenium", BotCategories.Automation),
    ("cypress", "Cypress", BotCategories.Automation),
    // HTTP libraries and tools
    ("curl/", "curl", BotCategories.Scripts),
    ("wget/", "Wget", BotCategories.Scripts),
    ("python-requests", "python-requests", BotCategories.Scripts),
    ("python-urllib", "python-urllib", BotCategories.Scripts),
    ("python-httpx", "httpx", BotCategories.Scripts),
    ("aiohttp", "aiohttp", BotCategories.Scripts),
    ("go-http-client", "Go-http-client", BotCategories.Scripts),
    ("okhttp", "okhttp", BotCategories.Scripts),
    ("apache-httpclient", "Apache-HttpClient", BotCategories.Scripts),
    ("java/", "Java", BotCategories.Scripts),
    ("axios/", "axios", BotCategories.Scripts),
    ("node-fetch", "node-fetch", BotCategories.Scripts),
    ("undici", "undici", BotCategories.Scripts),
    ("libwww-perl", "libwww-perl", BotCategories.Scripts),
    ("postmanruntime", "Postman", BotCategories.Scripts),
    ("insomnia", "Insomnia", BotCategories.Scripts),
    ("scrapy", "Scrapy", BotCategories.Scripts),
    ("http_request", "HTTP_Request", BotCategories.Scripts),
    ("wordpress/", "WordPress", BotCategories.Scripts),
    // Archives
    ("archive.org_bot", "archive.org_bot", BotCategories.Archiver),
    ("ia_archiver", "ia_archiver", BotCategories.Archiver),
  ];

  // A product token that names a bot and is followed by a version ("FooBot/1.2") or by an
  // info link ("compatible; FooCrawler; +https://..."). Device names such as "CUBOT X30"
  // are neither, so they do not match.
  [GeneratedRegex(@"(?<![A-Za-z0-9_.\-])([A-Za-z0-9_.\-]*(?:bot|crawler|spider|scraper|fetcher))(?:/|(?=[^/]*\+https?:))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex GenericBotToken();

  public BotInfo Detect(EventContext context) => cache.Get(context.UserAgent);

  internal static BotInfo DetectCore(string userAgent)
  {
    var lower = userAgent.ToLowerInvariant();
    foreach (var (token, name, category) in KnownBots)
    {
      if (lower.Contains(token, StringComparison.Ordinal))
        return new BotInfo(name, category);
    }
    var generic = GenericBotToken().Match(userAgent);
    if (generic.Success)
      return new BotInfo(generic.Groups[1].Value.TrimEnd('.', '-', '_'), BotCategories.Other);
    // Real browsers always identify as Mozilla-compatible; anything else is a program.
    if (!lower.StartsWith("mozilla/", StringComparison.Ordinal) &&
        !lower.StartsWith("opera/", StringComparison.Ordinal) &&
        !lower.Contains("applewebkit", StringComparison.Ordinal))
    {
      var slash = userAgent.IndexOfAny(['/', ' ', ';', '(']);
      var product = (slash > 0 ? userAgent[..slash] : userAgent).Trim();
      return new BotInfo(product.Length == 0 ? "Unknown" : product, BotCategories.Scripts);
    }
    return null;
  }
}
