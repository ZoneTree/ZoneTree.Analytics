namespace ZoneTree.Analytics.Web;

/// <summary>
/// Acquisition channels assigned to sessions.
/// </summary>
public static class Channels
{
  /// <summary>No referrer and no campaign parameters.</summary>
  public const string Direct = "Direct";

  /// <summary>Unpaid search engine traffic.</summary>
  public const string OrganicSearch = "Organic Search";

  /// <summary>Paid search campaigns.</summary>
  public const string PaidSearch = "Paid Search";

  /// <summary>Unpaid social network traffic.</summary>
  public const string OrganicSocial = "Organic Social";

  /// <summary>Paid social campaigns.</summary>
  public const string PaidSocial = "Paid Social";

  /// <summary>Other paid campaigns.</summary>
  public const string PaidOther = "Paid Other";

  /// <summary>Email clients and newsletters.</summary>
  public const string Email = "Email";

  /// <summary>Affiliate campaigns.</summary>
  public const string Affiliates = "Affiliates";

  /// <summary>AI assistants and answer engines.</summary>
  public const string AIAssistants = "AI Assistants";

  /// <summary>Video platforms.</summary>
  public const string Video = "Video";

  /// <summary>Links from other websites.</summary>
  public const string Referral = "Referral";
}

/// <summary>
/// Classifies referrers and campaign parameters into referrer host, source and channel.
/// </summary>
sealed class ReferrerClassifier
{
  enum Category
  {
    None,
    Search,
    Social,
    Email,
    AI,
    Video,
  }

  sealed record Known(string Name, Category Category);

  // Exact host or parent-domain matches. More specific hosts first.
  static readonly (string Host, string Name, Category Category)[] KnownHosts =
  [
    ("mail.google.com", "Gmail", Category.Email),
    ("gemini.google.com", "Gemini", Category.AI),
    ("bard.google.com", "Gemini", Category.AI),
    ("news.google.com", "Google News", Category.None),
    ("mail.yahoo.com", "Yahoo Mail", Category.Email),
    ("outlook.live.com", "Outlook", Category.Email),
    ("outlook.office.com", "Outlook", Category.Email),
    ("outlook.office365.com", "Outlook", Category.Email),
    ("mail.proton.me", "Proton Mail", Category.Email),
    ("search.brave.com", "Brave Search", Category.Search),
    ("news.ycombinator.com", "Hacker News", Category.Social),
    ("copilot.microsoft.com", "Copilot", Category.AI),
    ("chat.deepseek.com", "DeepSeek", Category.AI),
    ("chatgpt.com", "ChatGPT", Category.AI),
    ("chat.openai.com", "ChatGPT", Category.AI),
    ("perplexity.ai", "Perplexity", Category.AI),
    ("claude.ai", "Claude", Category.AI),
    ("bing.com", "Bing", Category.Search),
    ("duckduckgo.com", "DuckDuckGo", Category.Search),
    ("baidu.com", "Baidu", Category.Search),
    ("ecosia.org", "Ecosia", Category.Search),
    ("qwant.com", "Qwant", Category.Search),
    ("startpage.com", "Startpage", Category.Search),
    ("kagi.com", "Kagi", Category.Search),
    ("naver.com", "Naver", Category.Search),
    ("seznam.cz", "Seznam", Category.Search),
    ("sogou.com", "Sogou", Category.Search),
    ("ask.com", "Ask", Category.Search),
    ("facebook.com", "Facebook", Category.Social),
    ("fb.com", "Facebook", Category.Social),
    ("fb.me", "Facebook", Category.Social),
    ("messenger.com", "Facebook", Category.Social),
    ("instagram.com", "Instagram", Category.Social),
    ("t.co", "Twitter", Category.Social),
    ("twitter.com", "Twitter", Category.Social),
    ("x.com", "Twitter", Category.Social),
    ("linkedin.com", "LinkedIn", Category.Social),
    ("lnkd.in", "LinkedIn", Category.Social),
    ("reddit.com", "Reddit", Category.Social),
    ("threads.net", "Threads", Category.Social),
    ("bsky.app", "Bluesky", Category.Social),
    ("mastodon.social", "Mastodon", Category.Social),
    ("tiktok.com", "TikTok", Category.Social),
    ("snapchat.com", "Snapchat", Category.Social),
    ("tumblr.com", "Tumblr", Category.Social),
    ("vk.com", "VKontakte", Category.Social),
    ("weibo.com", "Weibo", Category.Social),
    ("quora.com", "Quora", Category.Social),
    ("discord.com", "Discord", Category.Social),
    ("t.me", "Telegram", Category.Social),
    ("telegram.org", "Telegram", Category.Social),
    ("whatsapp.com", "WhatsApp", Category.Social),
    ("youtube.com", "YouTube", Category.Video),
    ("youtu.be", "YouTube", Category.Video),
    ("vimeo.com", "Vimeo", Category.Video),
    ("twitch.tv", "Twitch", Category.Video),
    ("github.com", "GitHub", Category.None),
    ("stackoverflow.com", "Stack Overflow", Category.None),
  ];

  // Second-level names that appear under many country domains: google.com, google.co.uk, ...
  static readonly (string Label, string Name, Category Category)[] KnownLabels =
  [
    ("google", "Google", Category.Search),
    ("yahoo", "Yahoo!", Category.Search),
    ("yandex", "Yandex", Category.Search),
    ("pinterest", "Pinterest", Category.Social),
  ];

  static readonly Dictionary<string, Known> SourceAliases = BuildAliases();

  readonly Dictionary<string, string> customSources;

  readonly HashSet<string> internalHosts;

  public ReferrerClassifier(WebOptions options)
  {
    if (options.ReferrerSources != null)
    {
      customSources = new(StringComparer.OrdinalIgnoreCase);
      foreach (var (host, name) in options.ReferrerSources)
        customSources[UrlParser.NormalizeHost(host)] = name;
    }
    if (options.InternalHosts != null)
    {
      internalHosts = new(StringComparer.OrdinalIgnoreCase);
      foreach (var host in options.InternalHosts)
        internalHosts.Add(UrlParser.NormalizeHost(host));
    }
  }

  static Dictionary<string, Known> BuildAliases()
  {
    var aliases = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
    foreach (var (host, name, category) in KnownHosts)
    {
      aliases.TryAdd(host, new Known(name, category));
      aliases.TryAdd(name, new Known(name, category));
    }
    foreach (var (label, name, category) in KnownLabels)
    {
      aliases.TryAdd(label, new Known(name, category));
      aliases.TryAdd(name, new Known(name, category));
    }
    aliases.TryAdd("fb", new Known("Facebook", Category.Social));
    aliases.TryAdd("ig", new Known("Instagram", Category.Social));
    aliases.TryAdd("tw", new Known("Twitter", Category.Social));
    aliases.TryAdd("x", new Known("Twitter", Category.Social));
    aliases.TryAdd("li", new Known("LinkedIn", Category.Social));
    aliases.TryAdd("yt", new Known("YouTube", Category.Video));
    aliases.TryAdd("hn", new Known("Hacker News", Category.Social));
    aliases.TryAdd("newsletter", new Known("Newsletter", Category.Email));
    return aliases;
  }

  /// <summary>
  /// Classifies the traffic of a session's first event.
  /// </summary>
  public void Classify(
      string referrer,
      string pageHost,
      string utmSource,
      string utmMedium,
      out string referrerHost,
      out string source,
      out string channel)
  {
    referrerHost = UrlParser.GetHost(referrer);
    if (referrerHost.Length > 0 &&
        (referrerHost == pageHost || (internalHosts != null && internalHosts.Contains(referrerHost))))
      referrerHost = "";

    Known known = null;
    if (!string.IsNullOrWhiteSpace(utmSource))
    {
      utmSource = utmSource.Trim();
      if (SourceAliases.TryGetValue(utmSource, out known))
        source = known.Name;
      else if (TryMatchHost(UrlParser.NormalizeHost(utmSource), out known))
        source = known.Name;
      else
        source = utmSource;
    }
    else if (referrerHost.Length > 0)
    {
      if (customSources != null && TryMatchCustom(referrerHost, out var customName))
        source = customName;
      else if (TryMatchHost(referrerHost, out known))
        source = known.Name;
      else
        source = referrerHost;
    }
    else
    {
      source = Channels.Direct;
    }

    if (known == null && referrerHost.Length > 0)
      TryMatchHost(referrerHost, out known);
    channel = GetChannel(known?.Category ?? Category.None, utmMedium, utmSource, referrerHost);
  }

  bool TryMatchCustom(string host, out string name)
  {
    var current = host;
    while (true)
    {
      if (customSources.TryGetValue(current, out name))
        return true;
      var dot = current.IndexOf('.');
      if (dot < 0)
        return false;
      current = current[(dot + 1)..];
    }
  }

  static bool TryMatchHost(string host, out Known known)
  {
    known = null;
    if (string.IsNullOrEmpty(host))
      return false;
    foreach (var (knownHost, name, category) in KnownHosts)
    {
      if (host == knownHost ||
          (host.Length > knownHost.Length &&
           host.EndsWith(knownHost, StringComparison.Ordinal) &&
           host[host.Length - knownHost.Length - 1] == '.'))
      {
        known = new Known(name, category);
        return true;
      }
    }
    var labels = host.Split('.');
    for (var i = 0; i < labels.Length; ++i)
    {
      var remaining = labels.Length - i - 1;
      if (remaining < 1 || remaining > 2)
        continue;
      foreach (var (label, name, category) in KnownLabels)
      {
        if (labels[i] == label)
        {
          known = new Known(name, category);
          return true;
        }
      }
    }
    return false;
  }

  static string GetChannel(Category category, string utmMedium, string utmSource, string referrerHost)
  {
    var medium = utmMedium?.Trim().ToLowerInvariant() ?? "";
    var isPaid = medium is "cpc" or "ppc" or "cpm" or "cpv" or "cpa" or "paid" or
        "paidsearch" or "paid_search" or "paid-search" or "paidsocial" or "paid_social" or
        "paid-social" or "display" or "banner" or "retargeting" or "ads" or "ad" ||
        medium.StartsWith("paid", StringComparison.Ordinal);
    if (medium is "email" or "e-mail" or "e_mail" or "newsletter" || category == Category.Email)
      return Channels.Email;
    if (isPaid)
    {
      return category switch
      {
        Category.Search => Channels.PaidSearch,
        Category.Social => Channels.PaidSocial,
        _ => medium.Contains("social", StringComparison.Ordinal) ? Channels.PaidSocial :
             medium.Contains("search", StringComparison.Ordinal) || medium is "cpc" or "ppc"
               ? Channels.PaidSearch
               : Channels.PaidOther
      };
    }
    if (medium is "affiliate" or "affiliates" or "partner")
      return Channels.Affiliates;
    if (medium is "social" or "social-network" or "social-media" or "social_media" or "sm" ||
        category == Category.Social)
      return Channels.OrganicSocial;
    if (medium == "organic" || category == Category.Search)
      return Channels.OrganicSearch;
    if (category == Category.AI)
      return Channels.AIAssistants;
    if (medium == "video" || category == Category.Video)
      return Channels.Video;
    if (referrerHost.Length > 0 || !string.IsNullOrWhiteSpace(utmSource) || medium.Length > 0)
      return Channels.Referral;
    return Channels.Direct;
  }
}
