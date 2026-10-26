using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using ZoneTree.Analytics.AspNetCore;

namespace ZoneTree.Analytics.UnitTests;

// The dashboard's users, sessions, settings and engine host, on a real system store.
public sealed class DashboardAdministrationTests : DirectoryTest
{
  sealed class TestEnvironment(string root) : IWebHostEnvironment
  {
    public string WebRootPath { get; set; } = root;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ApplicationName { get; set; } = "ZoneTree.Analytics.Dashboard";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Production";
  }

  sealed class Host : IAsyncDisposable
  {
    public SystemStore Store;
    public UserStore Users;
    public AuditLog Audit;
    public EngineHost Engines;
    public SettingsService Settings;
    public SessionStore Sessions;
    public AnalyticsWebOptions Web = new();

    public Host(string root)
    {
      var environment = new TestEnvironment(root);
      Store = new SystemStore(Path.Combine(root, "system"));
      Users = new UserStore(Store);
      Audit = new AuditLog(Store);
      Engines = new EngineHost(environment, NullLoggerFactory.Instance);
      Settings = new SettingsService(Store, Engines, Web, Users, Audit, environment);
      Sessions = new SessionStore(Store, Users, Settings);
    }

    public async ValueTask DisposeAsync()
    {
      await Engines.DisposeAsync();
      Store.Dispose();
    }
  }

  static DashboardSettings Settings(string timeZone = "UTC")
  {
    var settings = SettingsService.CreateDefaults();
    settings.General.TimeZone = timeZone;
    // Tests download nothing.
    settings.Location.Database = LocationSettings.Off;
    settings.Sites.Items = [new() { Id = "shop", Name = "Shop", Domains = ["shop.example.com"] },
        new() { Id = "blog", Name = "Blog", Domains = ["*.blog.example.com"] }];
    settings.Sites.PrimarySite = "shop";
    return settings;
  }

  static UserChange NewUser(string name, UserRole role, params string[] sites) =>
      new(name, null, role, false, [.. sites], null, "a long enough password");

  static readonly HashSet<string> KnownSites = ["shop", "blog"];

  [Test]
  public void PasswordsVerifyOnlyTheirOwnText()
  {
    var hash = PasswordHasher.Hash("correct horse battery");
    Assert.Multiple(() =>
    {
      Assert.That(hash, Does.StartWith("pbkdf2-sha256$600000$"));
      Assert.That(PasswordHasher.Verify("correct horse battery", hash), Is.True);
      Assert.That(PasswordHasher.Verify("correct horse batterY", hash), Is.False);
      Assert.That(PasswordHasher.Verify("anything", null), Is.False, "an unknown user never signs in");
      Assert.That(PasswordHasher.Hash("correct horse battery"), Is.Not.EqualTo(hash), "every hash has its own salt");
      Assert.That(PasswordHasher.ValidateStrength("short", "user"), Is.Not.Null);
    });
  }

  [Test]
  public async Task TheDashboardHasExactlyOneAdministrator()
  {
    await using var host = new Host(directory);
    var admin = host.Users.Create(NewUser("Owner", UserRole.Administrator), KnownSites);
    var viewer = host.Users.Create(NewUser("viewer", UserRole.Viewer, "blog"), KnownSites);
    Assert.Multiple(() =>
    {
      Assert.Throws<UserStoreException>(() => host.Users.Create(NewUser("second", UserRole.Administrator), KnownSites));
      Assert.Throws<UserStoreException>(() => host.Users.Create(NewUser("OWNER", UserRole.Viewer), KnownSites), "names are unique ignoring case");
      Assert.Throws<UserStoreException>(() => host.Users.Update(viewer.Id, new(null, null, UserRole.Administrator, null, null, null, null), KnownSites));
      Assert.Throws<UserStoreException>(() => host.Users.Update(admin.Id, new(null, null, UserRole.Viewer, null, null, null, null), KnownSites));
      Assert.Throws<UserStoreException>(() => host.Users.Update(admin.Id, new(null, null, null, null, null, true, null), KnownSites));
      Assert.Throws<UserStoreException>(() => host.Users.Delete(admin.Id));
      Assert.Throws<UserStoreException>(() => host.Users.Create(NewUser("ghost", UserRole.Viewer, "missing"), KnownSites));
      Assert.That(host.Users.FindByName("owner").Id, Is.EqualTo(admin.Id));
    });
  }

  [Test]
  public async Task RolesAndSitesDecideWhatAUserReads()
  {
    await using var host = new Host(directory);
    var admin = host.Users.Create(NewUser("owner", UserRole.Administrator), KnownSites);
    var analyst = host.Users.Create(NewUser("analyst", UserRole.Analyst, "shop"), KnownSites);
    var viewer = host.Users.Create(new("viewer", null, UserRole.Viewer, true, null, null, "a long enough password"), KnownSites);
    Assert.Multiple(() =>
    {
      Assert.That(admin.CanAccess(SettingsMapper.DashboardUsageSite), Is.True);
      Assert.That(analyst.CanAccess("shop"), Is.True);
      Assert.That(analyst.CanAccess("blog"), Is.False);
      Assert.That(analyst.CanQuery, Is.True);
      Assert.That(viewer.CanAccess("blog"), Is.True, "all sites");
      Assert.That(viewer.CanAccess(SettingsMapper.DashboardUsageSite), Is.False, "reserved sites are the administrator's");
      Assert.That(viewer.CanAccess(DashboardAnalyticsOptions.PerformanceSite), Is.False);
      Assert.That(viewer.CanQuery, Is.False);
    });
  }

  [Test]
  public async Task SessionsKeepOnlyTokenHashesAndEndWithTheirUsersSecurityChanges()
  {
    await using var host = new Host(directory);
    var user = host.Users.Create(NewUser("analyst", UserRole.Analyst, "shop"), KnownSites);
    var context = new DefaultHttpContext();
    var (token, _) = host.Sessions.Create(user, context);
    var (other, _) = host.Sessions.Create(user, context);
    Assert.Multiple(() =>
    {
      Assert.That(host.Store.Contains("session/" + token), Is.False, "the token itself is never stored");
      Assert.That(host.Store.Contains("session/" + SessionStore.Id(token)), Is.True);
      Assert.That(host.Sessions.TryAuthenticate(token, out var id, out var signedIn), Is.True);
      Assert.That(signedIn.Id, Is.EqualTo(user.Id));
      Assert.That(host.Sessions.List(user.Id, id), Has.Count.EqualTo(2));
      Assert.That(host.Sessions.TryAuthenticate("forged", out _, out _), Is.False);
    });
    Assert.That(host.Sessions.Revoke(SessionStore.Id(other), "someone-else"), Is.False, "users end only their own sessions");
    Assert.That(host.Sessions.Revoke(SessionStore.Id(other), user.Id), Is.True);
    Assert.That(host.Sessions.TryAuthenticate(other, out _, out _), Is.False);
    host.Users.Update(user.Id, new(null, null, UserRole.Viewer, null, null, null, null), KnownSites);
    Assert.That(host.Sessions.TryAuthenticate(token, out _, out _), Is.False, "a role change ends every session");
    var (fresh, _) = host.Sessions.Create(host.Users.Get(user.Id), context);
    host.Users.Update(user.Id, new(null, null, null, null, null, true, null), KnownSites);
    Assert.That(host.Sessions.TryAuthenticate(fresh, out _, out _), Is.False, "a disabled user is signed out");
    Assert.That(host.Sessions.Cleanup(), Is.Zero, "ended sessions were already removed");
  }

  [Test]
  public async Task UsersAndSessionsSurviveARestart()
  {
    string token;
    await using (var host = new Host(directory))
    {
      var user = host.Users.Create(NewUser("owner", UserRole.Administrator), KnownSites);
      (token, _) = host.Sessions.Create(user, new DefaultHttpContext());
    }
    await using var reopened = new Host(directory);
    Assert.That(reopened.Users.HasAdministrator, Is.True);
    Assert.That(reopened.Sessions.TryAuthenticate(token, out _, out var signedIn), Is.True);
    Assert.That(signedIn.UserName, Is.EqualTo("owner"));
  }

  [Test]
  public void TheSchemaDescribesEverySettingAndPreset()
  {
    var schema = SettingsSchema.Document;
    var settings = SettingsService.CreateDefaults();
    var keys = new List<string>();
    void Visit(SettingField field)
    {
      if (field.Fields != null) { foreach (var child in field.Fields) Visit(child); return; }
      keys.Add(field.Key);
      Assert.DoesNotThrow(() => SettingsDocument.Read(settings, field.Key), field.Key);
    }
    foreach (var section in schema.Sections)
      foreach (var field in section.Fields)
        Visit(field);
    Assert.Multiple(() =>
    {
      Assert.That(schema.Sections.Select(section => section.Key), Has.Member("privacy").And.Member("indexes").And.Member("security"));
      Assert.That(keys, Does.Contain("storage.rawEventStore.writeAheadLogMode"));
      Assert.That(SettingsSchema.Find("general.timeZone").Tier, Is.EqualTo(SettingTier.Fixed));
      Assert.That(SettingsSchema.Find("reporting.maxConcurrentRequests").Tier, Is.EqualTo(SettingTier.Restart));
      foreach (var preset in schema.Presets)
        foreach (var key in preset.Values.Keys)
          Assert.That(keys, Does.Contain(key), $"{preset.Key} sets {key}");
    });
  }

  [Test]
  public void LocationSettingsMapToIpLocationSources()
  {
    var settings = SettingsService.CreateDefaults();
    var defaults = SettingsMapper.ToOptions(settings, directory, null).Web.IpLocation;
    settings.Location.Database = LocationSettings.DbIpCity;
    settings.Location.UpdateIntervalDays = 7;
    var city = SettingsMapper.ToOptions(settings, directory, null).Web.IpLocation;
    settings.Location.Database = LocationSettings.Custom;
    settings.Location.Urls = ["https://geo.example.com/countries.mmdb"];
    settings.Location.AutoUpdate = false;
    var custom = SettingsMapper.ToOptions(settings, directory, null).Web.IpLocation;
    settings.Location.Database = LocationSettings.Off;
    var off = SettingsMapper.ToOptions(settings, directory, null).Web.IpLocation;
    Assert.Multiple(() =>
    {
      Assert.That(defaults.Sources, Is.EqualTo(IpLocationSources.UserCountry));
      Assert.That(defaults.UpdateInterval, Is.EqualTo(TimeSpan.FromDays(1)));
      Assert.That(city.Sources, Is.EqualTo(IpLocationSources.DbIpCity));
      Assert.That(city.UpdateInterval, Is.EqualTo(TimeSpan.FromDays(7)));
      Assert.That(custom.Sources.Single().Url, Is.EqualTo("https://geo.example.com/countries.mmdb"));
      Assert.That(custom.UpdateInterval, Is.Null);
      Assert.That(off.Sources, Is.Empty);
    });
    settings.Location.Database = LocationSettings.Custom;
    settings.Location.Urls = [];
    Assert.That(Assert.Throws<SettingsValidationException>(() => SettingsMapper.Validate(settings, directory, Path.Combine(directory, "data/system"))).Key,
        Is.EqualTo("location.urls"));
    settings.Location.Urls = ["file:///etc/passwd"];
    Assert.That(Assert.Throws<SettingsValidationException>(() => SettingsMapper.Validate(settings, directory, Path.Combine(directory, "data/system"))).Key,
        Is.EqualTo("location.urls"));
  }

  [Test]
  public void SettingsMapToEngineOptions()
  {
    var settings = Settings("Europe/Berlin");
    settings.Retention.RawEventsDays = null;
    settings.Rollups.Granularities = [RollupGranularity.Minute, RollupGranularity.Day];
    settings.Privacy.RestrictDimensions = true;
    settings.Privacy.AllowedDimensions = ["plan"];
    settings.Queries.MaxDegreeOfParallelism = 0;
    settings.Storage.RollupStore.MutableSegmentMaxItemCount = 12_345;
    var options = SettingsMapper.ToOptions(settings, directory, null);
    Assert.Multiple(() =>
    {
      Assert.That(options.DataDirectory, Is.EqualTo(Path.GetFullPath(Path.Combine(directory, "data/analytics"))));
      Assert.That(options.TimeZone.Id, Is.EqualTo("Europe/Berlin"));
      Assert.That(options.DefaultSite, Is.EqualTo("shop"));
      Assert.That(options.Retention.RawEvents, Is.Null);
      Assert.That(options.Retention.HourRollups, Is.EqualTo(TimeSpan.FromDays(400)));
      Assert.That(options.Rollups.Granularities, Is.EqualTo(Granularity.Minute | Granularity.Day));
      Assert.That(options.Rollups.ValueAggregates["load_time"], Is.EqualTo(ValueAggregates.Percentiles));
      Assert.That(options.Privacy.AllowedDimensions, Is.EquivalentTo(new[] { "plan" }));
      Assert.That(options.Queries.MaxDegreeOfParallelism, Is.EqualTo(Environment.ProcessorCount));
      Assert.That(options.Storage.Rollups.MutableSegmentMaxItemCount, Is.EqualTo(12_345));
      Assert.That(options.Storage.DimensionIndexes.Dimensions, Is.EquivalentTo(DashboardIndexPreset.Dimensions));
      Assert.That(options.Storage.RollupIndexes.Definitions, Has.Count.EqualTo(12));
      Assert.That(options.Web.Bots, Is.EqualTo(BotTraffic.TrackSeparately));
    });
    Assert.DoesNotThrow(() => SettingsMapper.Validate(settings, directory, Path.Combine(directory, "system")));
  }

  [TestCase("timezone")]
  [TestCase("site-id")]
  [TestCase("reserved-site")]
  [TestCase("duplicate-domain")]
  [TestCase("range")]
  [TestCase("rollup")]
  [TestCase("overlap")]
  public void InvalidSettingsAreRejectedWithTheirField(string problem)
  {
    var settings = Settings();
    var expectedKey = problem switch
    {
      "timezone" => "general.timeZone",
      "range" => "sessions.timeoutMinutes",
      "overlap" => "general.dataDirectory",
      "rollup" => null,
      _ => "sites.items",
    };
    switch (problem)
    {
      case "timezone": settings.General.TimeZone = "Mars/Olympus"; break;
      case "site-id": settings.Sites.Items[0].Id = "Shop Front"; break;
      case "reserved-site": settings.Sites.Items[0].Id = SettingsMapper.DashboardUsageSite; break;
      case "duplicate-domain": settings.Sites.Items[1].Domains = ["shop.example.com"]; break;
      case "range": settings.Sessions.TimeoutMinutes = 0; break;
      case "rollup": settings.Rollups.Definitions.Add(new() { Dimensions = ["page", "page"] }); break;
      case "overlap": settings.General.DataDirectory = "system/analytics"; break;
    }
    var error = Assert.Throws<SettingsValidationException>(() => SettingsMapper.Validate(settings, directory, Path.Combine(directory, "system")));
    Assert.That(error.Key, Is.EqualTo(expectedKey), error.Message);
  }

  [Test]
  public async Task SavingSettingsReopensTheEngineAndKeepsFixedChoices()
  {
    await using (var host = new Host(directory))
    {
      await host.Settings.CompleteSetupAsync(Settings("Europe/Berlin"));
      Assert.That(host.Engines.State, Is.EqualTo(EngineState.Running));

      var noLoss = host.Settings.Copy();
      noLoss.Indexes.Dimensions.Add("browser");
      noLoss.General.RefreshSeconds = 60;
      var review = host.Settings.Review(noLoss);
      Assert.That(review.Changes.Select(change => (change.Key, change.Tier)),
          Is.EquivalentTo(new[] { ("general.refreshSeconds", SettingTier.Live), ("indexes.dimensions", SettingTier.Reopen) }));
      var saved = await host.Settings.SaveAsync(noLoss, "owner", false, null);
      Assert.That(saved.Reopened, Is.True);
      Assert.That(host.Engines.State, Is.EqualTo(EngineState.Running));

      var shorter = host.Settings.Copy();
      shorter.Retention.RawEventsDays = 7;
      var warning = Assert.ThrowsAsync<SettingsConfirmationRequiredException>(() => host.Settings.SaveAsync(shorter, "owner", false, null));
      Assert.That(warning.Warnings, Has.One.Contains("older than 7 days"));
      await host.Settings.SaveAsync(shorter, "owner", true, null);

      var moved = host.Settings.Copy();
      moved.General.TimeZone = "UTC";
      var fixedError = Assert.Throws<SettingsValidationException>(() => host.Settings.Review(moved));
      Assert.That(fixedError.Key, Is.EqualTo("general.timeZone"));

      var live = host.Settings.Copy();
      live.Tracking.AllowedClientEvents = ["signup"];
      live.Reporting.MaxConcurrentRequests = 9;
      var liveResult = await host.Settings.SaveAsync(live, "owner", false, null);
      Assert.Multiple(() =>
      {
        Assert.That(liveResult.Reopened, Is.False);
        Assert.That(liveResult.RestartRequired, Is.True);
        Assert.That(host.Web.AllowedClientEvents, Is.EquivalentTo(new[] { "signup" }));
      });
    }
    await using var reopened = new Host(directory);
    Assert.Multiple(() =>
    {
      Assert.That(reopened.Settings.IsConfigured, Is.True);
      Assert.That(reopened.Settings.Current.Indexes.Dimensions, Does.Contain("browser"));
      Assert.That(reopened.Settings.Current.Retention.RawEventsDays, Is.EqualTo(7));
      Assert.That(reopened.Web.AllowedClientEvents, Is.EquivalentTo(new[] { "signup" }), "live settings apply at startup");
      Assert.That(reopened.Audit.List(null, 10).Select(entry => entry.Action), Is.EqualTo(Enumerable.Repeat("settings.updated", 3)));
    });
  }

  [Test]
  public async Task SettingsTheEngineCannotOpenLeaveThePreviousOnesRunning()
  {
    await using var host = new Host(directory);
    await host.Settings.CompleteSetupAsync(Settings());
    var previous = host.Settings.Current;
    var broken = host.Settings.Copy();
    // The dataset stores its time zone: the engine refuses another one when it opens.
    broken.General.TimeZone = "Europe/Berlin";
    Assert.ThrowsAsync<AnalyticsConfigurationException>(() => host.Engines.ReopenAsync(broken, previous));
    Assert.That(host.Engines.State, Is.EqualTo(EngineState.Running));
    using var lease = await host.Engines.AcquireAsync(CancellationToken.None);
    Assert.That(lease.Engine.TimeZone.Id, Is.EqualTo("UTC"));
  }

  [TestCase("https://shop.example.com", "shop")]
  [TestCase("https://news.blog.example.com", "blog")]
  [TestCase("https://blog.example.com", "blog")]
  [TestCase("https://unknown.test", "shop")]
  [TestCase("http://dashboard.local", null)]
  public void BeaconsReportToTheSiteOfTheirPage(string origin, string site)
  {
    var settings = Settings();
    SettingsMapper.Validate(settings, directory, null);
    var context = new DefaultHttpContext();
    context.Request.Host = new HostString("dashboard.local");
    context.Request.Headers.Origin = origin;
    Assert.That(new SiteResolution().Resolve(context, settings), Is.EqualTo(site));
    settings.Sites.UnmatchedTraffic = UnmatchedTraffic.Ignore;
    settings.Sites.TrackDashboardUsage = true;
    var expected = site == "shop" && origin.Contains("unknown") ? null : site ?? SettingsMapper.DashboardUsageSite;
    Assert.That(new SiteResolution().Resolve(context, settings), Is.EqualTo(expected));
  }

  [Test]
  public void SetupKeysMatchOnlyTheirOwnValueUntilRetired()
  {
    var key = new SetupKey();
    var value = key.Value;
    Assert.Multiple(() =>
    {
      Assert.That(value, Does.Match("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$"));
      Assert.That(key.Matches(value.ToLowerInvariant().Replace("-", " ")), Is.True, "case and separators do not matter");
      Assert.That(key.Matches("AAAA-BBBB-CCCC"), Is.False);
    });
    key.Retire();
    Assert.That(key.Matches(value), Is.False);
  }

  [TestCase("http://app.example.com:8080", "blog")]
  [TestCase("http://example.com:8080", "blog")]
  [TestCase("http://app.example.com:9090", null)]
  [TestCase("http://app.example.com", null)]
  public void WildcardDomainsWithAPortMatchOnlyThatPort(string origin, string site)
  {
    var settings = Settings();
    settings.Sites.Items[1].Domains = ["*.example.com:8080"];
    settings.Sites.UnmatchedTraffic = UnmatchedTraffic.Ignore;
    SettingsMapper.Validate(settings, directory, null);
    var context = new DefaultHttpContext();
    context.Request.Host = new HostString("dashboard.local");
    context.Request.Headers.Origin = origin;
    Assert.That(new SiteResolution().Resolve(context, settings), Is.EqualTo(site));
  }

  [Test]
  public void MissingListsBecomeEmptyAndEmptyEntriesNameTheirField()
  {
    var json = System.Text.Json.JsonSerializer.Serialize(Settings(), SystemStore.Json)
        .Replace("\"internalHosts\":[]", "\"internalHosts\":null")
        .Replace("\"referrerSources\":{}", "\"referrerSources\":null");
    var settings = System.Text.Json.JsonSerializer.Deserialize<DashboardSettings>(json, SystemStore.Json);
    Assert.That(settings.Tracking.InternalHosts, Is.Null);
    Assert.DoesNotThrow(() => SettingsMapper.Validate(settings, directory, null));
    Assert.That(settings.Tracking.InternalHosts, Is.Empty);
    Assert.That(settings.Tracking.ReferrerSources, Is.Empty);

    settings.Rollups.Granularities = null;
    var granularities = Assert.Throws<SettingsValidationException>(() => SettingsMapper.Validate(settings, directory, null));
    Assert.That(granularities.Key, Is.EqualTo("rollups.granularities"), "an empty list is still invalid where one is required");

    settings = Settings();
    settings.Rollups.ValueAggregates.Add(null);
    Assert.That(Assert.Throws<SettingsValidationException>(() => SettingsMapper.Validate(settings, directory, null)).Key,
        Is.EqualTo("rollups.valueAggregates"));
    settings = Settings();
    settings.Storage.RollupStore = null;
    Assert.That(Assert.Throws<SettingsValidationException>(() => SettingsMapper.Validate(settings, directory, null)).Key,
        Is.EqualTo("storage.rollupStore"));
  }

  [Test]
  public async Task AuditEntriesPastTheirRetentionOrBeyondTheCapAreRemoved()
  {
    await using var host = new Host(directory);
    for (var i = 0; i < 3; i++) host.Audit.Write("old", "auth.signin-failed", $"{i}");
    Thread.Sleep(20);
    var cutoff = DateTime.UtcNow;
    Thread.Sleep(20);
    for (var i = 0; i < 4; i++) host.Audit.Write("new", "auth.signin", $"{i}");

    Assert.That(host.Audit.Prune(cutoff), Is.EqualTo(3), "entries older than the retention");
    Assert.That(host.Audit.List(null, 100).Select(entry => entry.Actor), Is.EqualTo(Enumerable.Repeat("new", 4)));

    Assert.That(host.Audit.Prune(cutoff, maxEntries: 2), Is.EqualTo(2), "the oldest beyond the cap");
    Assert.That(host.Audit.List(null, 100).Select(entry => entry.Detail), Is.EqualTo(new[] { "3", "2" }), "the newest are kept");

    var settings = host.Settings;
    await settings.CompleteSetupAsync(Settings());
    var shorter = settings.Copy();
    shorter.Security.AuditRetentionDays = 30;
    Assert.That(settings.Review(shorter).Warnings, Has.One.Contains("Audit log entries older than 30 days"));
  }

  [Test]
  public async Task SignInAttemptsAreCountedBeforeTheirPasswordsAreChecked()
  {
    await using var host = new Host(directory);
    var throttle = new SignInThrottle(host.Settings);
    var limit = host.Settings.Security.MaxFailedSignIns;
    var context = new DefaultHttpContext();
    context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
    // Attempts arriving together: only the limit gets through to the password check.
    var admitted = Enumerable.Range(0, 50).AsParallel().Count(_ => throttle.Begin("signin", "admin", context) == null);
    Assert.That(admitted, Is.EqualTo(limit));

    // Rotating user names from one address runs into the address's own limit.
    var other = new DefaultHttpContext();
    other.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.8");
    var names = Enumerable.Range(0, 100).Count(n => throttle.Begin("signin", $"user{n}", other) == null);
    Assert.That(names, Is.EqualTo(limit * 4));

    // A success clears its name and is taken back from the address.
    var fresh = new DefaultHttpContext();
    fresh.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
    for (var attempt = 0; attempt < limit * 3; attempt++)
    {
      Assert.That(throttle.Begin("signin", "owner", fresh), Is.Null);
      throttle.Succeeded("signin", "owner", fresh);
    }
  }
}
