using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class IpLocationProviderTests : DirectoryTest
{
  const string CountryUrl = "https://databases.test/latest/countries.mmdb";

  static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

  static byte[] Countries(string gb = "GB")
  {
    var writer = new MmdbWriter(6);
    writer.Insert("81.2.69.0/24", writer.CountryCode(gb));
    writer.Insert("2a01:4f8::/32", writer.CountryCode("DE"));
    return writer.ToArray();
  }

  static byte[] Cities(bool ipv4)
  {
    var writer = new MmdbWriter(ipv4 ? 4 : 6);
    if (ipv4) writer.Insert("5.1.0.0/16", writer.SapicsCity("DE", "Berlin", "Spandau"));
    else writer.Insert("2a02:1::/32", writer.MaxMindCity("DE", "Bavaria", "Munich"));
    return writer.ToArray();
  }

  static IpLocationOptions Options(DatabaseServer server, params IpLocationSource[] sources)
  {
    var options = new IpLocationOptions { HttpClient = new HttpClient(server) };
    options.Sources.AddRange(sources.Length == 0 ? [new(CountryUrl)] : sources);
    return options;
  }

  string CountryPath => Path.Combine(directory, "ip-location", "countries.mmdb");

  void WriteCountries(DateTime checkedAt, byte[] content = null)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(CountryPath));
    File.WriteAllBytes(CountryPath, content ?? Countries());
    File.SetLastWriteTimeUtc(CountryPath, checkedAt);
  }

  [Test]
  public async Task DownloadsAMissingDatabase()
  {
    var server = new DatabaseServer();
    server.Publish(CountryUrl, Countries(), DateTimeOffset.UtcNow.AddHours(-3));
    await using var provider = new IpLocationProvider(Options(server), baseDirectory: directory);
    await provider.WaitForDatabaseAsync().WaitAsync(Patience);
    Assert.Multiple(() =>
    {
      Assert.That(provider.Paths, Is.EqualTo(new[] { CountryPath }));
      Assert.That(provider.Locate("81.2.69.160")?.Country, Is.EqualTo("GB"));
      Assert.That(provider.Locate("2a01:4f8::1")?.Country, Is.EqualTo("DE"));
      Assert.That(File.ReadAllBytes(CountryPath), Is.EqualTo(Countries()));
      // The check is dated by the server's clock.
      Assert.That(File.GetLastWriteTimeUtc(CountryPath), Is.EqualTo(server.Now.UtcDateTime).Within(TimeSpan.FromSeconds(1)));
      Assert.That(server.Requests.Single().IfModifiedSince, Is.Null);
      Assert.That(Directory.GetFiles(Path.GetDirectoryName(CountryPath)), Has.Length.EqualTo(1));
      Assert.That(provider.LastError, Is.Null);
      Assert.That(provider.LastChecked, Is.Not.Null);
    });
  }

  [Test]
  public async Task ExistingFilesLoadAtOnceAndAreNotCheckedEarly()
  {
    WriteCountries(DateTime.UtcNow.AddHours(-1));
    var server = new DatabaseServer();
    await using var provider = new IpLocationProvider(Options(server), baseDirectory: directory);
    await provider.WaitForDatabaseAsync().WaitAsync(Patience);
    Assert.That(provider.Locate("81.2.69.160")?.Country, Is.EqualTo("GB"));
    await Task.Delay(300);
    Assert.That(server.Requests, Is.Empty, "checked an hour ago, due in a day");
  }

  [Test]
  public async Task StaleFilesAreCheckedAtStart()
  {
    WriteCountries(DateTime.UtcNow.AddDays(-2));
    var server = new DatabaseServer();
    server.Publish(CountryUrl, Countries(gb: "IE"), DateTimeOffset.UtcNow.AddHours(-1));
    await using var provider = new IpLocationProvider(Options(server), baseDirectory: directory);
    await Until(() => provider.Locate("81.2.69.160")?.Country == "IE");
    Assert.That(server.Requests.Single().IfModifiedSince, Is.Not.Null);
  }

  [Test]
  public async Task AServerClockBehindDoesNotRepeatChecks()
  {
    // A check dates the file with the server's clock, two days behind: it looks due again at once.
    WriteCountries(DateTime.UtcNow.AddDays(-3));
    var server = new DatabaseServer { Now = DateTimeOffset.UtcNow.AddDays(-2) };
    server.Publish(CountryUrl, Countries(), DateTimeOffset.UtcNow.AddDays(-4));
    await using var provider = new IpLocationProvider(Options(server), baseDirectory: directory);
    await Until(() => !server.Requests.IsEmpty);
    await Task.Delay(500);
    Assert.That(server.Requests, Has.Count.EqualTo(1));
    Assert.That(server.Requests.Single().Status, Is.EqualTo(HttpStatusCode.NotModified));
  }

  [Test]
  public async Task UpdatesUseConditionalRequestsAndSwapDatabases()
  {
    var published = DateTimeOffset.UtcNow.AddDays(-1);
    WriteCountries(DateTime.UtcNow.AddHours(-1));
    var server = new DatabaseServer();
    server.Publish(CountryUrl, Countries(), published);
    await using var provider = new IpLocationProvider(Options(server), baseDirectory: directory);
    await provider.WaitForDatabaseAsync().WaitAsync(Patience);
    var first = provider.Database;

    Assert.That(await provider.UpdateAsync(), Is.False);
    Assert.Multiple(() =>
    {
      Assert.That(server.Requests.Last().IfModifiedSince, Is.Not.Null);
      Assert.That(server.Requests.Last().Status, Is.EqualTo(HttpStatusCode.NotModified));
      Assert.That(provider.Database, Is.SameAs(first));
      // A not modified answer dates the check.
      Assert.That(File.GetLastWriteTimeUtc(CountryPath), Is.EqualTo(server.Now.UtcDateTime).Within(TimeSpan.FromSeconds(1)));
    });

    server.Now = server.Now.AddMinutes(5);
    server.Publish(CountryUrl, Countries(gb: "IE"), server.Now.AddMinutes(-1));
    Assert.That(await provider.UpdateAsync(), Is.True);
    Assert.Multiple(() =>
    {
      Assert.That(server.Requests.Last().Status, Is.EqualTo(HttpStatusCode.OK));
      Assert.That(provider.Locate("81.2.69.160")?.Country, Is.EqualTo("IE"));
      Assert.That(first.Locate("81.2.69.160")?.Country, Is.EqualTo("GB"), "a database does not change");
    });
  }

  [TestCase("invalid")]
  [TestCase("too large")]
  [TestCase("server error")]
  public async Task FailedDownloadsKeepTheCurrentDatabase(string failure)
  {
    WriteCountries(DateTime.UtcNow.AddHours(-1));
    var server = new DatabaseServer();
    var options = Options(server);
    options.MaxDownloadBytes = 4096;
    switch (failure)
    {
      case "invalid": server.Publish(CountryUrl, Encoding.UTF8.GetBytes("<html>Not found</html>"), DateTimeOffset.UtcNow); break;
      case "too large": server.Publish(CountryUrl, new byte[8192], DateTimeOffset.UtcNow); break;
      default: server.Failures = 1; break;
    }
    var logger = new RecordingLogger();
    await using var provider = new IpLocationProvider(options, baseDirectory: directory, logger: logger);
    await provider.WaitForDatabaseAsync().WaitAsync(Patience);
    var current = provider.Database;
    Assert.That(await provider.UpdateAsync(), Is.False);
    Assert.Multiple(() =>
    {
      Assert.That(provider.Database, Is.SameAs(current));
      Assert.That(provider.LastError, Does.Contain(CountryUrl));
      Assert.That(logger.Problems, Has.Count.EqualTo(1));
      Assert.That(File.ReadAllBytes(CountryPath), Is.EqualTo(Countries()));
      Assert.That(Directory.GetFiles(Path.GetDirectoryName(CountryPath)), Has.Length.EqualTo(1), "no partial download is left");
    });
  }

  [Test]
  public async Task FailedDownloadsWaitADayUnlessUpdatedAtOnce()
  {
    var server = new DatabaseServer { Failures = 1 };
    server.Publish(CountryUrl, Countries(), DateTimeOffset.UtcNow);
    await using var provider = new IpLocationProvider(Options(server), baseDirectory: directory);
    await Until(() => provider.LastError != null);
    await Task.Delay(300);
    Assert.That(server.Requests, Has.Count.EqualTo(1), "no retry before a day");
    Assert.That(await provider.UpdateAsync(), Is.True);
    Assert.Multiple(() =>
    {
      Assert.That(server.Requests, Has.Count.EqualTo(2));
      Assert.That(provider.LastError, Is.Null);
      Assert.That(provider.Locate("81.2.69.160")?.Country, Is.EqualTo("GB"));
    });
  }

  [Test]
  public async Task DamagedFilesAreDownloadedAgainInFull()
  {
    WriteCountries(DateTime.UtcNow, Encoding.UTF8.GetBytes("damaged"));
    var server = new DatabaseServer();
    server.Publish(CountryUrl, Countries(), DateTimeOffset.UtcNow.AddDays(-1));
    await using var provider = new IpLocationProvider(Options(server), baseDirectory: directory);
    await Until(() => provider.LastError != null);
    Assert.That(provider.Database, Is.Null);
    Assert.That(provider.LastError, Does.Contain("Loading"));
    Assert.That(await provider.UpdateAsync(), Is.True);
    Assert.That(server.Requests.Single().IfModifiedSince, Is.Null, "a damaged file is not kept as current");
    Assert.That(provider.Locate("81.2.69.160")?.Country, Is.EqualTo("GB"));
  }

  [Test]
  public async Task MissingFilesWaitWhenDownloadsAreOff()
  {
    var server = new DatabaseServer();
    server.Publish(CountryUrl, Countries(), DateTimeOffset.UtcNow);
    var options = Options(server);
    options.DownloadMissing = false;
    await using var provider = new IpLocationProvider(options, baseDirectory: directory);
    await Task.Delay(300);
    Assert.That(server.Requests, Is.Empty);
    Assert.That(provider.Locate("81.2.69.160"), Is.Null);
    Assert.That(await provider.UpdateAsync(), Is.True, "an explicit update downloads");
    Assert.That(provider.Locate("81.2.69.160")?.Country, Is.EqualTo("GB"));
  }

  [Test]
  public async Task SeparateFilesResolveCitiesToThePrecision()
  {
    const string ipv4 = "https://databases.test/latest/city-ipv4.mmdb";
    const string ipv6 = "https://databases.test/latest/city-ipv6.mmdb";
    var server = new DatabaseServer();
    server.Publish(ipv4, Cities(ipv4: true), DateTimeOffset.UtcNow);
    server.Publish(ipv6, Cities(ipv4: false), DateTimeOffset.UtcNow);
    var options = Options(server, new(ipv4), new(ipv6, "custom/v6.mmdb"));
    options.Directory = Path.Combine(directory, "geo");
    await using var provider = new IpLocationProvider(options, GeoPrecision.Region, logger: new RecordingLogger());
    await provider.WaitForDatabaseAsync().WaitAsync(Patience);
    Assert.Multiple(() =>
    {
      Assert.That(provider.Paths, Is.EqualTo(new[]
      {
        Path.Combine(directory, "geo", "city-ipv4.mmdb"), Path.Combine(directory, "geo", "custom", "v6.mmdb")
      }));
      Assert.That(provider.Locate("5.1.0.1"), Is.EqualTo(new GeoLocation("DE", "Berlin")));
      Assert.That(provider.Locate("2a02:1::1"), Is.EqualTo(new GeoLocation("DE", "Bavaria")));
      Assert.That(provider.Database.Precision, Is.EqualTo(GeoPrecision.Region));
    });
  }

  [Test]
  public async Task DisposingCancelsADownload()
  {
    var server = new DatabaseServer { Block = new() };
    server.Publish(CountryUrl, Countries(), DateTimeOffset.UtcNow);
    var provider = new IpLocationProvider(Options(server), baseDirectory: directory);
    await Until(() => !server.Requests.IsEmpty);
    await provider.DisposeAsync().AsTask().WaitAsync(Patience);
    Assert.ThrowsAsync<ObjectDisposedException>(() => provider.UpdateAsync());
    Assert.That(File.Exists(CountryPath), Is.False);
  }

  [Test]
  public void OptionsAreValidated()
  {
    var server = new DatabaseServer();
    Assert.Multiple(() =>
    {
      Assert.Throws<ArgumentException>(() => _ = new IpLocationProvider(new IpLocationOptions()));
      Assert.Throws<ArgumentOutOfRangeException>(() => _ = new IpLocationProvider(Options(server), GeoPrecision.None));
      Assert.Throws<ArgumentException>(() => _ = new IpLocationProvider(Options(server, new IpLocationSource("ftp://databases.test/a.mmdb"))));
      Assert.Throws<ArgumentException>(() => _ = new IpLocationProvider(Options(server, new IpLocationSource("https://databases.test/"))));
      Assert.Throws<ArgumentException>(() => _ = new IpLocationProvider(Options(server, new IpLocationSource(null))));
      Assert.Throws<ArgumentException>(() => _ = new IpLocationProvider(Options(server,
          new IpLocationSource("https://databases.test/..%2F..%2Fsystem%2Fsettings"))), "an escaped path stays in the directory");
      Assert.Throws<ArgumentException>(() => _ = new IpLocationProvider(Options(server,
          new IpLocationSource("https://databases.test/a%5Cb.mmdb"))));
      Assert.Throws<ArgumentException>(() => _ = new IpLocationProvider(Options(server,
          new("https://a.test/countries.mmdb"), new("https://b.test/countries.mmdb")), baseDirectory: directory));
      var interval = Options(server);
      interval.UpdateInterval = TimeSpan.FromHours(23);
      Assert.Throws<ArgumentException>(() => _ = new IpLocationProvider(interval));
      var retry = Options(server);
      retry.RetryInterval = TimeSpan.FromHours(23);
      Assert.Throws<ArgumentException>(() => _ = new IpLocationProvider(retry));

      var both = TestEngine.Options(directory);
      both.Web.IpLocation.Sources.Add(new(CountryUrl));
      Assert.Throws<ArgumentException>(both.Validate, "a geolocator and sources");
    });
  }

  [Test]
  public async Task EngineRunsAProviderForItsSources()
  {
    var server = new DatabaseServer();
    server.Publish(CountryUrl, Countries(), DateTimeOffset.UtcNow);
    IpLocationProvider provider;
    var at = new DateTime(2026, 3, 2, 12, 0, 0);
    using (var engine = TestEngine.Create(directory, options =>
    {
      options.Web.GeoLocator = null;
      options.Web.IpLocation.Sources.AddRange(Options(server).Sources);
      options.Web.IpLocation.HttpClient = new HttpClient(server);
      options.Privacy.GeoPrecision = GeoPrecision.Country;
    }))
    {
      provider = engine.IpLocation;
      await provider.WaitForDatabaseAsync().WaitAsync(Patience);
      Assert.That(provider.Paths.Single(), Is.EqualTo(Path.Combine(Path.GetFullPath(directory), "ip-location", "countries.mmdb")));
      engine.Track(new AnalyticsEvent(StandardEvents.PageView)
      {
        Url = "https://mysite.com/", IpAddress = "81.2.69.160", UserAgent = UserAgents.ChromeWindows, Timestamp = Utc(at)
      });
      engine.Flush();
      var countries = await engine.Web.GetBreakdownAsync(new WebReportRequest { Range = Day(at.Date) }, StandardDimensions.Country);
      Assert.That(countries.Rows.Single().Value, Is.EqualTo("GB"));
    }
    Assert.ThrowsAsync<ObjectDisposedException>(() => provider.UpdateAsync(), "the engine disposes its provider");

    // Without geography, no provider runs.
    using var reopened = TestEngine.Create(Path.Combine(directory, "without-geography"), options =>
    {
      options.Web.GeoLocator = null;
      options.Web.IpLocation.Sources.Add(new(CountryUrl));
      options.Privacy.GeoPrecision = GeoPrecision.None;
    });
    Assert.That(reopened.IpLocation, Is.Null);
  }

  static async Task Until(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow + Patience;
    while (!condition())
    {
      if (DateTime.UtcNow > deadline) Assert.Fail("The condition was not met in time.");
      await Task.Delay(20);
    }
  }

  /// <summary>
  /// Serves database files like a release server: conditional requests, a Date header, and on
  /// request failures or requests that wait until cancelled.
  /// </summary>
  sealed class DatabaseServer : HttpMessageHandler
  {
    readonly ConcurrentDictionary<string, (byte[] Content, DateTimeOffset LastModified)> files = new();

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public int Failures;

    public TaskCompletionSource Block { get; init; }

    public ConcurrentQueue<(string Url, DateTimeOffset? IfModifiedSince, HttpStatusCode Status)> Requests { get; } = new();

    public void Publish(string url, byte[] content, DateTimeOffset lastModified) => files[url] = (content, lastModified);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      var url = request.RequestUri.ToString();
      var since = request.Headers.IfModifiedSince;
      HttpResponseMessage response;
      if (Interlocked.Decrement(ref Failures) >= 0)
        response = new(HttpStatusCode.InternalServerError);
      else if (!files.TryGetValue(url, out var file))
        response = new(HttpStatusCode.NotFound);
      else if (since != null && file.LastModified <= since)
        response = new(HttpStatusCode.NotModified);
      else
        response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Content) { Headers = { LastModified = file.LastModified } } };
      response.Headers.Date = Now;
      Requests.Enqueue((url, since, response.StatusCode));
      if (Block != null) await Block.Task.WaitAsync(cancellationToken);
      return response;
    }
  }
}
