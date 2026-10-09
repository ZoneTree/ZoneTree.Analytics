using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class UserAgentParserTests
{
  readonly IUserAgentParser parser = UserAgentParser.Default;

  [TestCase(
      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
      "Chrome", "126", "Windows", "10", "Desktop")]
  [TestCase(
      "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1",
      "Safari", "17", "iOS", "17.4", "Mobile")]
  [TestCase(
      "Mozilla/5.0 (X11; Linux x86_64; rv:127.0) Gecko/20100101 Firefox/127.0",
      "Firefox", "127", "Linux", "", "Desktop")]
  [TestCase(
      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.2535.67",
      "Edge", "125", "Windows", "10", "Desktop")]
  [TestCase(
      "Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/24.0 Chrome/117.0.0.0 Mobile Safari/537.36",
      "Samsung Internet", "24", "Android", "14", "Mobile")]
  [TestCase(
      "Mozilla/5.0 (iPad; CPU OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/126.0.6478.54 Mobile/15E148 Safari/604.1",
      "Chrome", "126", "iOS", "16.6", "Tablet")]
  [TestCase(
      "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
      "Safari", "17", "macOS", "10.15", "Desktop")]
  [TestCase(
      "Mozilla/5.0 (Linux; Android 13; Pixel Tablet) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
      "Chrome", "126", "Android", "13", "Tablet")]
  public void ParsesBrowsers(string ua, string browser, string browserVersion, string os, string osVersion, string device)
  {
    var info = parser.Parse(ua);
    Assert.Multiple(() =>
    {
      Assert.That(info.Browser, Is.EqualTo(browser));
      Assert.That(info.BrowserVersion, Is.EqualTo(browserVersion));
      Assert.That(info.OperatingSystem, Is.EqualTo(os));
      Assert.That(info.OperatingSystemVersion, Is.EqualTo(osVersion));
      Assert.That(info.Device, Is.EqualTo(device));
    });
  }

  [TestCase(null)]
  [TestCase("")]
  public void ReturnsNullWithoutUserAgent(string ua)
  {
    Assert.That(parser.Parse(ua), Is.Null);
  }
}

public sealed class UserAgentBotDetectorTests
{
  static BotInfo Detect(string userAgent) =>
      BotDetector.UserAgent.Detect(new EventContext { UserAgent = userAgent });

  [TestCase("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "Googlebot", BotCategories.Search)]
  [TestCase("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)", "Bingbot", BotCategories.Search)]
  [TestCase("Mozilla/5.0 (compatible; YandexBot/3.0; +http://yandex.com/bots)", "YandexBot", BotCategories.Search)]
  [TestCase("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)", "GPTBot", BotCategories.AI)]
  [TestCase("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; ClaudeBot/1.0; +claudebot@anthropic.com)", "ClaudeBot", BotCategories.AI)]
  [TestCase("Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)", "AhrefsBot", BotCategories.Seo)]
  [TestCase("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)", "facebookexternalhit", BotCategories.SocialPreview)]
  [TestCase("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)", "Slackbot", BotCategories.SocialPreview)]
  [TestCase("Mozilla/5.0+(compatible; UptimeRobot/2.0; http://www.uptimerobot.com/)", "UptimeRobot", BotCategories.Monitoring)]
  [TestCase("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36", "HeadlessChrome", BotCategories.Automation)]
  [TestCase("curl/8.4.0", "curl", BotCategories.Scripts)]
  [TestCase("python-requests/2.31.0", "python-requests", BotCategories.Scripts)]
  [TestCase("Go-http-client/1.1", "Go-http-client", BotCategories.Scripts)]
  [TestCase("Mozilla/5.0 (compatible; ExampleCrawler/1.0)", "ExampleCrawler", BotCategories.Other)]
  [TestCase("Mozilla/5.0 (compatible; SomeSpider; +https://spider.example.com)", "SomeSpider", BotCategories.Other)]
  public void DetectsBots(string ua, string name, string category)
  {
    Assert.That(Detect(ua), Is.EqualTo(new BotInfo(name, category)));
  }

  [TestCase("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36")]
  [TestCase("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1")]
  [TestCase("Mozilla/5.0 (Linux; Android 13; CUBOT X30) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36")]
  [TestCase("Mozilla/5.0 (X11; Linux x86_64; rv:127.0) Gecko/20100101 Firefox/127.0")]
  [TestCase("")]
  [TestCase(null)]
  public void IgnoresBrowsers(string ua)
  {
    Assert.That(Detect(ua), Is.Null);
  }
}

public sealed class UrlParserTests
{
  [Test]
  public void ParsesAbsoluteUrls()
  {
    var url = UrlParser.Parse(
        "https://www.Example.com/blog/post/?utm_source=newsletter&utm_medium=email&utm_campaign=launch&id=5#top",
        new WebOptions(),
        includeQueryString: false);
    Assert.Multiple(() =>
    {
      Assert.That(url.Hostname, Is.EqualTo("example.com"));
      Assert.That(url.Path, Is.EqualTo("/blog/post"));
      Assert.That(url.UtmSource, Is.EqualTo("newsletter"));
      Assert.That(url.UtmMedium, Is.EqualTo("email"));
      Assert.That(url.UtmCampaign, Is.EqualTo("launch"));
    });
  }

  [Test]
  public void KeepsQueryStringWithoutUtmWhenRequested()
  {
    var url = UrlParser.Parse("/search?q=zonetree&utm_source=x", new WebOptions(), includeQueryString: true);
    Assert.Multiple(() =>
    {
      Assert.That(url.Path, Is.EqualTo("/search?q=zonetree"));
      Assert.That(url.UtmSource, Is.EqualTo("x"));
      Assert.That(url.Hostname, Is.EqualTo(""));
    });
  }

  [Test]
  public void AppliesPathOptions()
  {
    var web = new WebOptions { LowercasePaths = true };
    Assert.That(UrlParser.Parse("https://a.com/Users/123/", web, false).Path, Is.EqualTo("/users/123"));
    Assert.That(UrlParser.Parse("https://a.com", web, false).Path, Is.EqualTo("/"));
  }

  [Test]
  public void RefParameterIsSourceFallback()
  {
    var url = UrlParser.Parse("/?ref=producthunt", new WebOptions(), false);
    Assert.That(url.UtmSource, Is.EqualTo("producthunt"));
  }
}

public sealed class ReferrerClassifierTests
{
  readonly ReferrerClassifier classifier = new(new WebOptions
  {
    ReferrerSources = new Dictionary<string, string> { ["lobste.rs"] = "Lobsters" }
  });

  [TestCase("https://www.google.com/", null, null, "google.com", "Google", "Organic Search")]
  [TestCase("https://www.google.co.uk/search?q=x", null, null, "google.co.uk", "Google", "Organic Search")]
  [TestCase("https://t.co/abc", null, null, "t.co", "Twitter", "Organic Social")]
  [TestCase("https://news.ycombinator.com/item?id=1", null, null, "news.ycombinator.com", "Hacker News", "Organic Social")]
  [TestCase("https://chatgpt.com/", null, null, "chatgpt.com", "ChatGPT", "AI Assistants")]
  [TestCase("https://blog.example.org/post", null, null, "blog.example.org", "blog.example.org", "Referral")]
  [TestCase("https://lobste.rs/s/abc", null, null, "lobste.rs", "Lobsters", "Referral")]
  [TestCase(null, null, null, "", "Direct", "Direct")]
  [TestCase("https://mysite.com/other", null, null, "", "Direct", "Direct")]
  [TestCase(null, "google", "cpc", "", "Google", "Paid Search")]
  [TestCase("https://www.facebook.com/", "facebook", "paid_social", "facebook.com", "Facebook", "Paid Social")]
  [TestCase(null, "newsletter", "email", "", "Newsletter", "Email")]
  [TestCase("https://mail.google.com/", null, null, "mail.google.com", "Gmail", "Email")]
  public void Classifies(string referrer, string utmSource, string utmMedium, string host, string source, string channel)
  {
    classifier.Classify(referrer, "mysite.com", utmSource, utmMedium, out var referrerHost, out var actualSource, out var actualChannel);
    Assert.Multiple(() =>
    {
      Assert.That(referrerHost, Is.EqualTo(host));
      Assert.That(actualSource, Is.EqualTo(source));
      Assert.That(actualChannel, Is.EqualTo(channel));
    });
  }
}
