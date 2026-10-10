using ZoneTree.Analytics.Web;
using ZoneTree.Options;

namespace ZoneTree.Analytics.UnitTests;

static class TestEngine
{
  public static string NewDirectory(string name) =>
      Path.Combine(TestContext.CurrentContext.WorkDirectory, "data", name + "-" + Guid.NewGuid().ToString("N")[..8]);

  public static AnalyticsOptions Options(string directory, Action<AnalyticsOptions> configure = null)
  {
    var options = new AnalyticsOptions
    {
      DataDirectory = directory,
      Web = { GeoLocator = new StubGeoLocator() }
    };
    UseTestDefaults(options);
    configure?.Invoke(options);
    return options;
  }

  /// <summary>
  /// Settings every test engine uses: data is kept forever, and tests flush explicitly.
  /// </summary>
  public static void UseTestDefaults(AnalyticsOptions options)
  {
    options.Retention.RawEvents = null;
    options.Retention.MinuteRollups = null;
    options.Retention.HourRollups = null;
    options.Retention.DayRollups = null;
    options.Retention.CheckInterval = null;
    options.Ingestion.FlushInterval = TimeSpan.FromHours(1);
    options.Ingestion.QueueFullBehavior = QueueFullBehavior.Wait;
  }

  /// <summary>
  /// Applies retention and returns the number of partitions it dropped.
  /// </summary>
  public static long ApplyRetentionCountingDrops(this AnalyticsEngine engine)
  {
    var before = engine.GetStatistics().PartitionsDropped;
    engine.ApplyRetention();
    return engine.GetStatistics().PartitionsDropped - before;
  }

  public static AnalyticsEngine Create(string directory, Action<AnalyticsOptions> configure = null) =>
      new(Options(directory, configure));

  public static void Delete(string directory)
  {
    try
    {
      if (Directory.Exists(directory))
        Directory.Delete(directory, true);
    }
    catch (IOException)
    {
    }
  }
}

sealed class StubGeoLocator : IGeoLocator
{
  public GeoLocation Locate(string ipAddress) => ipAddress switch
  {
    "10.0.0.1" => new GeoLocation("DE", "Berlin", "Berlin"),
    "10.0.0.2" => new GeoLocation("US", "California", "San Francisco"),
    "10.0.0.3" => new GeoLocation("DE", "Bavaria", "Munich"),
    _ => null
  };
}

static class UserAgents
{
  public const string ChromeWindows =
      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

  public const string SafariIPhone =
      "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1";

  public const string Googlebot =
      "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";
}

/// <summary>
/// Records the errors and warnings that the engine and ZoneTree log.
/// </summary>
sealed class RecordingLogger : ZoneTree.Logger.ILogger
{
  public System.Collections.Concurrent.ConcurrentQueue<string> Problems { get; } = new();

  public ZoneTree.Logger.LogLevel LogLevel { get; set; } = ZoneTree.Logger.LogLevel.Warning;

  public void LogError(Exception log) => Problems.Enqueue(log.ToString());

  public void LogWarning(object log) => Problems.Enqueue(log?.ToString());

  public void LogInfo(object log)
  {
  }

  public void LogTrace(object log)
  {
  }
}
