using ZoneTree.Analytics.Web;
using ZoneTree.Options;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// A fixture with its own data directory, created before and deleted after each test. Fixture
/// teardowns run before the directory is deleted, so they can still dispose their engines.
/// </summary>
public abstract class DirectoryTest
{
  protected string directory;

  [SetUp]
  public void CreateDirectory() => directory = TestEngine.NewDirectory(GetType().Name);

  [TearDown]
  public void DeleteDirectory() => TestEngine.Delete(directory);
}

/// <summary>
/// Time helpers shared by the tests, imported into every file.
/// </summary>
static class TestTime
{
  /// <summary>The UTC instant of a date and time.</summary>
  public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

  /// <summary>The absolute range [from, to) in UTC.</summary>
  public static DateRange Range(DateTime from, DateTime to) => DateRange.Between(Utc(from), Utc(to));

  /// <summary>The 24 hours that start at <paramref name="day"/>, in UTC.</summary>
  public static DateRange Day(DateTime day) => Range(day, day.AddDays(1));
}

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

  /// <summary>
  /// Opens an engine and waits until its background index work is done, so tests see the same
  /// plans as a host that waits for readiness.
  /// </summary>
  public static AnalyticsEngine Create(string directory, Action<AnalyticsOptions> configure = null)
  {
    var engine = new AnalyticsEngine(Options(directory, configure));
    try
    {
      engine.WaitForIndexesAsync().WaitAsync(TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
    }
    catch
    {
      engine.Dispose();
      throw;
    }
    return engine;
  }

  public static void Delete(string directory)
  {
    var path = Path.GetFullPath(directory);
    var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "data")) + Path.DirectorySeparatorChar;
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (!path.StartsWith(root, comparison))
      throw new InvalidOperationException($"Test cleanup path '{path}' is outside '{root}'.");
    for (var attempt = 0; ; ++attempt)
    {
      try
      {
        if (Directory.Exists(path))
          Directory.Delete(path, true);
        return;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        if (attempt == 4)
          throw new IOException($"Test cleanup could not delete '{path}' after five attempts.", exception);
        Thread.Sleep(50);
      }
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
