#nullable enable
using ZoneTree.Analytics;

// Streaming generation through the real engine; memory does not grow with the
// requested event count. Inputs include page views, goals, purchases and bots.
static class SampleTraffic
{
  public static Task SeedAsync(AnalyticsEngine engine) => GenerateAsync(engine, 10_000);

  public static async Task GenerateAsync(
      AnalyticsEngine engine, int eventCount, int days = 60, string? site = null,
      Action<int>? progress = null, int workers = 1, CancellationToken cancellationToken = default)
  {
    if (eventCount is < 1 or > 10_000_000 || days is < 1 or > 90 || workers is < 1 or > 64)
      throw new ArgumentException("Choose 1–10,000,000 events, 1–90 days, and 1–64 workers.");
    workers = Math.Min(workers, eventCount);
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var progressSync = new object();
    var processed = 0;
    var end = DateTimeOffset.UtcNow.AddSeconds(-1);
    async Task ProduceAsync(int count)
    {
      try
      {
        // Each producer owns a visitor stream, so parallel writes cannot reorder
        // events for the same visitor. Only a bounded number of tasks is created.
        foreach (var item in CreateEvents(count, days, site, engine.TimeZone, end))
        {
          stop.Token.ThrowIfCancellationRequested();
          if (!await engine.TrackAsync(item, stop.Token))
            throw new InvalidOperationException("The engine did not accept the generated event.");
          var accepted = Interlocked.Increment(ref processed);
          if (accepted % 1000 == 0)
            lock (progressSync) progress?.Invoke(Volatile.Read(ref processed));
        }
      }
      catch { stop.Cancel(); throw; }
    }
    try
    {
      if (workers == 1) await ProduceAsync(eventCount);
      else await Task.WhenAll(Enumerable.Range(0, workers).Select(worker =>
          Task.Run(() => ProduceAsync(eventCount / workers + (worker < eventCount % workers ? 1 : 0)))));
      await engine.FlushAsync(cancellationToken);
    }
    finally { progress?.Invoke(Volatile.Read(ref processed)); }
  }

  public static IEnumerable<AnalyticsEvent> CreateEvents(
      int eventCount, int days, string? site = null, TimeZoneInfo? timeZone = null,
      DateTimeOffset? endingAt = null)
  {
    if (eventCount is < 1 or > 10_000_000)
      throw new ArgumentOutOfRangeException(nameof(eventCount), "Choose between 1 and 10,000,000 events.");
    if (days is < 1 or > 90)
      throw new ArgumentOutOfRangeException(nameof(days), "Choose between 1 and 90 days.");
    var random = new Random(42);
    var batch = Guid.NewGuid().ToString("N");
    var end = endingAt ?? DateTimeOffset.UtcNow.AddSeconds(-1);
    var start = end.AddDays(-days);
    var profile = new SampleTrafficProfile(start, end, timeZone ?? TimeZoneInfo.Utc);
    string[] paths = ["/", "/docs", "/pricing", "/blog", "/integrations", "/docs/getting-started"];
    string[] referrers = ["", "https://www.google.com/", "https://github.com/", "https://www.bing.com/"];
    const string desktop = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36";
    const string mobile = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1";
    var index = 0;
    var session = 0;
    while (index < eventCount)
    {
      var moment = profile.At((index + random.NextDouble()) / eventCount);
      var campaign = moment.Campaign / (1 + moment.Campaign);
      var bot = random.NextDouble() < 0.025;
      var returning = session > 1024 && moment.Timestamp - start > TimeSpan.FromHours(1)
          && random.NextDouble() < 0.25;
      var visitor = returning ? random.Next(session / 2) : session;
      var onMobile = visitor % 10 < 4;
      var bounce = !bot && random.NextDouble() < 0.4 + (onMobile ? 0.06 : 0) - campaign * 0.18;
      var pageCount = bounce ? 1 : random.Next(2, 7);
      var signup = !bot && !bounce && random.NextDouble() < 0.08 + campaign * 0.16;
      var purchase = signup && random.NextDouble() < 0.2 + campaign * 0.12;
      var length = Math.Min(eventCount - index, pageCount + (signup ? 1 : 0) + (purchase ? 1 : 0));
      var source = random.NextDouble();
      var referrer = campaign > 0.3 && source < 0.55 ? referrers[2]
          : source < 0.45 ? referrers[1] : source < 0.8 ? referrers[0]
          : source < 0.92 ? referrers[2] : referrers[3];
      var timestamp = moment.Timestamp;
      for (var offset = 0; offset < length; offset++)
      {
        var name = offset < pageCount ? "pageview" : offset == pageCount ? "signup" : "purchase";
        var path = offset >= pageCount ? "/pricing" : bot ? paths[random.Next(paths.Length)]
            : offset == 0 ? (campaign > 0.3 ? "/blog" : source < 0.45 ? "/docs" : "/")
            : offset == 1 ? "/pricing" : paths[random.Next(paths.Length)];
        var item = new AnalyticsEvent(name)
        {
          Site = site,
          Timestamp = timestamp,
          VisitorId = bot ? null : $"sample-{batch}-{visitor}",
          Url = "https://sample.example" + path,
          Referrer = referrer,
          IpAddress = visitor % 5 < 3 ? "203.0.113.1" : "203.0.113.2",
          UserAgent = bot ? "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"
              : onMobile ? mobile : desktop,
        };
        if (name == "purchase")
        {
          var team = random.NextDouble() < 0.22;
          item.Dimensions["plan"] = team ? "team" : "pro";
          item.Values["revenue"] = team ? 149 : 49;
        }
        if (name == "pageview")
          item.Values["load_time"] = (int)(120 + Math.Pow(random.NextDouble(), 3) * 1100 + campaign * 250);
        yield return item;
        // Real visit lengths, instead of stretching one visit over hours or days.
        timestamp = timestamp.AddSeconds(random.Next(5, 46));
        if (timestamp > end) timestamp = end;
      }
      index += length;
      session++;
    }
  }
}
