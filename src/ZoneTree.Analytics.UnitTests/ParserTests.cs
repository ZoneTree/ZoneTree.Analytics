using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class UserAgentParserTests
{
  readonly UserAgentParser parser = new();

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
      Assert.That(info.IsBot, Is.False);
      Assert.That(info.Browser, Is.EqualTo(browser));
      Assert.That(info.BrowserVersion, Is.EqualTo(browserVersion));
      Assert.That(info.OperatingSystem, Is.EqualTo(os));
      Assert.That(info.OperatingSystemVersion, Is.EqualTo(osVersion));
      Assert.That(info.Device, Is.EqualTo(device));
    });
  }

  [TestCase("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
  [TestCase("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
  [TestCase("curl/8.4.0")]
  [TestCase("python-requests/2.31.0")]
  [TestCase("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36")]
  [TestCase("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)")]
  [TestCase("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
  public void DetectsBots(string ua)
  {
    Assert.That(parser.Parse(ua).IsBot, Is.True);
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
    var web = new WebOptions
    {
      LowercasePaths = true,
      PathNormalizer = path => System.Text.RegularExpressions.Regex.Replace(path, "/\\d+", "/:id")
    };
    Assert.That(UrlParser.Parse("https://a.com/Users/123/", web, false).Path, Is.EqualTo("/users/:id"));
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
