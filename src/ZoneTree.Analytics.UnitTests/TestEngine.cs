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
      Retention = new RetentionOptions
      {
        RawEvents = null,
        MinuteRollups = null,
        HourRollups = null,
        DayRollups = null,
        CheckInterval = null
      },
      Ingestion = new IngestionOptions
      {
        // Tests flush explicitly.
        FlushInterval = TimeSpan.FromHours(1),
        QueueFullBehavior = QueueFullBehavior.Wait
      },
      Web = new WebOptions
      {
        GeoLocator = new StubGeoLocator()
      }
    };
    configure?.Invoke(options);
    return options;
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
