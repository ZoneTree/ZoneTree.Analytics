using System.Diagnostics;
using System.Globalization;
using ZoneTree.Analytics;
using ZoneTree.Analytics.Web;

// Generates synthetic web traffic, measures ingestion throughput and query latency,
// and prints a small dashboard.
//
// dotnet run -c Release -- [events=1000000] [days=30] [workers=1] [mode=full]
//
// mode can combine: noraw, nosessions, totalsonly, norollups (for profiling components).
// Set ZT_PAUSE=1 to pause after ingestion, for example to attach dotnet-gcdump.

var eventCount = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 1_000_000;
var days = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 30;
var workers = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1;
var directory = Path.Combine(AppContext.BaseDirectory, "data", "playground");
if (Directory.Exists(directory))
  Directory.Delete(directory, true);

var mode = args.Length > 3 ? args[3] : "full";
var options = new AnalyticsOptions
{
  DataDirectory = directory,
  Ingestion = { WorkerCount = workers, QueueFullBehavior = QueueFullBehavior.Wait },
  Web = { GeoLocator = new SyntheticGeoLocator() }
};
if (mode.Contains("noraw"))
  options.Storage.StoreRawEvents = false;
if (mode.Contains("nosessions"))
  options.Sessions.Enabled = false;
if (mode.Contains("totalsonly"))
  options.Rollups.Definitions = [new RollupDefinition()];
if (mode.Contains("norollups"))
  options.Rollups.Granularities = Granularity.None;

Console.WriteLine($"Generating {eventCount:N0} page views over {days} days with {workers} worker(s)...");
var traffic = new SyntheticTraffic(days, eventCount, seed: 42);
var events = new AnalyticsEvent[eventCount];
for (var i = 0; i < events.Length; ++i)
  events[i] = traffic.Next();
var from = traffic.Start;
var to = traffic.Start.AddDays(days);

var process = Process.GetCurrentProcess();
using (var engine = new AnalyticsEngine(options))
{
  var stopwatch = Stopwatch.StartNew();
  foreach (var e in events)
    engine.Track(e);
  var queued = stopwatch.Elapsed;
  engine.Flush();
  var elapsed = stopwatch.Elapsed;
  var statistics = engine.GetStatistics();
  process.Refresh();
  Console.WriteLine($"Queued in {queued.TotalSeconds:F2}s, processed and flushed in {elapsed.TotalSeconds:F2}s");
  Console.WriteLine($"Throughput: {eventCount / elapsed.TotalSeconds:N0} events/s");
  Console.WriteLine($"Processed={statistics.EventsProcessed:N0} bots={statistics.BotsExcluded:N0} sessions={statistics.SessionsStarted:N0} errors={statistics.Errors}");
  var heapBefore = GC.GetTotalMemory(false);
  events = null;
  var liveHeap = GC.GetTotalMemory(true);
  Console.WriteLine($"Working set: {process.WorkingSet64 / 1024 / 1024:N0} MB, heap: {heapBefore / 1024 / 1024:N0} MB, live heap after GC (events released): {liveHeap / 1024 / 1024:N0} MB");
  if (Environment.GetEnvironmentVariable("ZT_PAUSE") == "1")
  {
    Console.WriteLine("PID " + Environment.ProcessId + " paused");
    Thread.Sleep(TimeSpan.FromSeconds(40));
  }
  Console.WriteLine();

  T Measure<T>(string name, Func<T> query)
  {
    query(); // warm up
    var sw = Stopwatch.StartNew();
    const int runs = 5;
    T result = default;
    for (var i = 0; i < runs; ++i)
      result = query();
    Console.WriteLine($"{name,-44} {sw.Elapsed.TotalMilliseconds / runs,9:F2} ms");
    return result;
  }

  Console.WriteLine("Query latency (average of 5 runs):");
  DateRange Range(DateTime start, DateTime end) =>
      DateRange.Between(new DateTimeOffset(start, TimeSpan.Zero), new DateTimeOffset(end, TimeSpan.Zero));
  WebReportRequest Request(DateTime start, DateTime end) => new() { Range = Range(start, end) };
  var month = Request(from, to);
  var summary = Measure("Summary (30 days)", () => engine.Web.GetSummaryAsync(month).Result);
  Measure("Summary vs previous period", () => engine.Web.GetSummaryAsync(new WebReportRequest { Range = month.Range, Compare = Comparison.PreviousPeriod }).Result);
  var timeseries = Measure("Daily time series (30 days)", () => engine.Web.GetTimeseriesAsync(month, TimeInterval.Day).Result);
  Measure("Hourly time series (7 days)", () => engine.Web.GetTimeseriesAsync(Request(to.AddDays(-7), to), TimeInterval.Hour).Result);
  var pages = Measure("Top pages (30 days)", () => engine.Web.GetPagesAsync(month).Result);
  var sources = Measure("Top sources (30 days)", () => engine.Web.GetBreakdownAsync(month, StandardDimensions.Source).Result);
  var countries = Measure("Countries (30 days)", () => engine.Web.GetBreakdownAsync(month, StandardDimensions.Country).Result);
  Measure("Devices (30 days)", () => engine.Web.GetBreakdownAsync(month, StandardDimensions.Device).Result);
  Measure("Unaligned range (rollups + raw edges)", () => engine.Web.GetSummaryAsync(Request(from.AddHours(7.5), to.AddHours(-3.25))).Result);
  AnalyticsQuery AdHoc(DateTime start) => new()
  {
    Range = Range(start, to),
    GroupBy = { StandardDimensions.Page },
    Filters = { Filter.Equal(StandardDimensions.Country, "DE"), Filter.Equal(StandardDimensions.Device, "Mobile") },
    Limit = 10
  };
  Measure("Ad-hoc: pages where country=DE, device=Mobile (1 day, raw)", () => engine.Query(AdHoc(to.AddDays(-1))));
  Measure("Ad-hoc: same query over 30 days (raw)", () => engine.Query(AdHoc(from)));
  Measure("Segment: visitors who viewed /pricing (7 days, raw)", () => engine.Query(new AnalyticsQuery
  {
    Range = Range(to.AddDays(-7), to),
    Segments = { Segment.VisitorsWhoViewed("/pricing") },
    Metrics = { Metric.Visitors, Metric.PageViews }
  }));
  Measure("Funnel: / -> /pricing -> /signup (7 days, raw)", () => engine.QueryFunnelAsync(new FunnelQuery
  {
    Range = Range(to.AddDays(-7), to),
    Steps = { FunnelStep.Page("Home", "/"), FunnelStep.Page("Pricing", "/pricing"), FunnelStep.Page("Signup", "/signup") }
  }).Result);

  Console.WriteLine();
  Console.WriteLine($"Visitors {summary.Visitors:N0} | Page views {summary.PageViews:N0} | Sessions {summary.Sessions:N0} | " +
      $"Bounce rate {summary.BounceRate:P1} | Avg. duration {summary.AverageSessionDuration:mm\\:ss} | Pages/session {summary.PageViewsPerSession:F2}");
  Console.WriteLine();
  Console.WriteLine("Top pages:");
  foreach (var page in pages.Rows)
    Console.WriteLine($"  {page.Page,-24} {page.PageViews,10:N0} views {page.Visitors,9:N0} visitors  exit rate {page.ExitRate:P0}");
  Console.WriteLine("Top sources:");
  foreach (var source in sources.Rows)
    Console.WriteLine($"  {source.Value,-24} {source.Visitors,10:N0} visitors  bounce {source.BounceRate:P0}");
  Console.WriteLine("Countries:");
  foreach (var country in countries.Rows)
    Console.WriteLine($"  {country.Value,-24} {country.Visitors,10:N0} visitors");
  Console.WriteLine("Last 7 days:");
  foreach (var point in timeseries.TakeLast(7))
    Console.WriteLine($"  {point.Bucket:yyyy-MM-dd} {point.Summary.Visitors,9:N0} visitors {point.Summary.PageViews,10:N0} views");
}

var size = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length);
Console.WriteLine();
Console.WriteLine($"Data directory size: {size / 1024.0 / 1024.0:F1} MB ({(double)size / eventCount:F0} bytes/event)");

sealed class SyntheticGeoLocator : IGeoLocator
{
  static readonly string[] Countries = ["US", "DE", "GB", "FR", "IN", "BR", "JP", "CA", "NL", "TR"];

  public GeoLocation Locate(string ipAddress)
  {
    var hash = (uint)ipAddress.GetHashCode();
    // Skew: a few countries dominate.
    var index = (int)(hash % 100) switch
    {
      < 35 => 0,
      < 50 => 1,
      < 60 => 2,
      var x => 3 + x % 7
    };
    return new GeoLocation(Countries[index]);
  }
}

sealed class SyntheticTraffic
{
  static readonly string[] Pages =
  [
    "/", "/pricing", "/docs", "/docs/getting-started", "/docs/api", "/blog",
    "/blog/zonetree-analytics", "/blog/lsm-trees", "/about", "/contact", "/signup", "/login",
  ];

  static readonly string[] Referrers =
  [
    null, null, null, "https://www.google.com/", "https://www.google.de/", "https://www.bing.com/",
    "https://t.co/x", "https://news.ycombinator.com/", "https://www.reddit.com/r/dotnet",
    "https://github.com/ZoneTree", "https://chatgpt.com/", "https://dev.to/some-post",
  ];

  static readonly string[] UserAgents =
  [
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
    "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1",
    "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36",
    "Mozilla/5.0 (X11; Linux x86_64; rv:127.0) Gecko/20100101 Firefox/127.0",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.2535.67",
    "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
  ];

  readonly Random random;

  readonly int days;

  readonly int totalEvents;

  readonly int visitorPool;

  readonly Dictionary<int, (DateTime Last, int Page)> visitorState = [];

  public SyntheticTraffic(int days, int totalEvents, int seed)
  {
    this.days = days;
    this.totalEvents = totalEvents;
    random = new Random(seed);
    visitorPool = 50_000;
    Start = DateTime.UtcNow.Date.AddDays(-days);
  }

  public DateTime Start { get; }

  long cursorTicks;

  public AnalyticsEvent Next()
  {
    // Events advance through time in order, like live traffic.
    cursorTicks += (long)(random.NextDouble() * 2 * TimeSpan.TicksPerDay * days / totalEvents);
    var timestamp = Start.AddTicks(Math.Min(cursorTicks, TimeSpan.TicksPerDay * days - 1));
    var visitor = (int)(Math.Pow(random.NextDouble(), 2) * visitorPool); // repeat visitors are common
    var ua = UserAgents[visitor % UserAgents.Length];
    var ip = $"10.{visitor / 65536 % 256}.{visitor / 256 % 256}.{visitor % 256}";
    string referrer;
    int page;
    if (visitorState.TryGetValue(visitor, out var state) && timestamp - state.Last < TimeSpan.FromMinutes(20))
    {
      // Continue browsing: move to a related page.
      page = (state.Page + random.Next(1, 4)) % Pages.Length;
      referrer = "https://example.com" + Pages[state.Page];
    }
    else
    {
      page = random.Next(10) < 6 ? 0 : random.Next(Pages.Length);
      referrer = Referrers[random.Next(Referrers.Length)];
    }
    visitorState[visitor] = (timestamp, page);
    var url = "https://example.com" + Pages[page];
    if (random.Next(50) == 0)
      url += "?utm_source=newsletter&utm_medium=email&utm_campaign=october";
    return new AnalyticsEvent(StandardEvents.PageView)
    {
      Url = url,
      Referrer = referrer,
      IpAddress = ip,
      UserAgent = ua,
      Timestamp = timestamp
    };
  }
}
