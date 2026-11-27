using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using PureGate.WebSocket;
using ZoneTree.Analytics;

sealed record DashboardLiveUpdate(long? Visitors, bool ReportsChanged);

// Each watched site shares a sample across its subscribers. The stream owns neither an engine
// lease nor a queue of past samples while it waits for the next update.
sealed class DashboardLiveReports(IHostApplicationLifetime lifetime)
{
  public const string Path = "/_dashboard/live";
  readonly ConcurrentDictionary<string, Site> sites = new(StringComparer.Ordinal);
  readonly Lock subscriptions = new();

  public static WebSocketEndpointBuilder Map(IEndpointRouteBuilder app)
  {
    var endpoint = app.MapPureGateWebSocket(Path, options =>
    {
      // Reconnect through ASP.NET Core authentication and read a fresh snapshot.
      options.ResumeWindow = TimeSpan.Zero;
      options.MaxStreams = 2;
    });
    endpoint.RequireAuthorization();
    endpoint.MapStream<string, DashboardLiveUpdate>("reports.watch", static (site, call, cancellationToken) =>
        call.Services.GetRequiredService<DashboardLiveReports>().WatchAsync(site,
            call.User.FindFirst(DashboardAuthentication.SessionClaim)?.Value,
            call.Services.GetRequiredService<SessionStore>(),
            call.Services.GetRequiredService<SettingsService>(),
            call.Services.GetRequiredService<EngineHost>(), cancellationToken));
    return endpoint;
  }

  public void MarkChanged(string site)
  {
    if (sites.TryGetValue(site, out var state))
      Interlocked.Increment(ref state.Revision);
  }

  public async IAsyncEnumerable<DashboardLiveUpdate> WatchAsync(string site, string sessionId, SessionStore sessions,
      SettingsService settings, EngineHost engines, [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
    cancellationToken = stopping.Token;
    RequireSite(site, sessionId, sessions, settings);
    Site state;
    lock (subscriptions)
    {
      state = sites.GetOrAdd(site, static _ => new Site());
      ++state.Readers;
    }

    try
    {
      Sample previous = null;
      var sendFollowUp = false;
      while (true)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var seconds = settings.Current.General.RefreshSeconds;
        if (seconds > 0)
        {
          var current = await state.ReadAsync(engines, site, TimeSpan.FromSeconds(seconds), lifetime.ApplicationStopping)
              .WaitAsync(cancellationToken).ConfigureAwait(false);
          RequireSite(site, sessionId, sessions, settings);
          var isFirstSample = previous == null;
          var reportsChanged = isFirstSample || current.Version != previous.Version;
          if (reportsChanged || sendFollowUp || current.Visitors != previous.Visitors)
            yield return new DashboardLiveUpdate(current.Visitors, reportsChanged);
          // Let a client that already loaded reports reconcile once at the next sample.
          sendFollowUp = isFirstSample;
          previous = current;
        }
        else
        {
          RequireSite(site, sessionId, sessions, settings);
          previous = null;
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds > 0 ? Math.Min(seconds, 30) : 30), cancellationToken).ConfigureAwait(false);
        RequireSite(site, sessionId, sessions, settings);
      }
    }
    finally
    {
      lock (subscriptions)
      {
        if (--state.Readers == 0)
          sites.TryRemove(site, out _);
      }
    }
  }

  static void RequireSite(string site, string sessionId, SessionStore sessions, SettingsService settings)
  {
    if (!sessions.TryAuthenticateId(sessionId, out var user))
      throw new CallException("session_ended", "Sign in to continue.");
    var configured = settings.Current?.Sites;
    var exists = configured != null && (configured.Items.Any(item => item.Id == site)
        || site == SettingsMapper.DashboardUsageSite && configured.TrackDashboardUsage);
    if (!exists || !user.CanAccess(site))
      throw new CallException("site_forbidden", "Access to this site is not allowed.");
  }

  sealed record ReportVersion(long Engine, long Writes, DateOnly Day, string IndexMaintenance);

  sealed record Sample(long? Visitors, ReportVersion Version);

  sealed class Site
  {
    readonly Lock sampling = new();
    Task<Sample> sample;
    long sampledAt;
    public int Readers;
    public long Revision;

    public Task<Sample> ReadAsync(EngineHost engines, string id, TimeSpan interval, CancellationToken stopping)
    {
      lock (sampling)
      {
        if (sample == null || sample.IsCompleted && Stopwatch.GetElapsedTime(sampledAt) >= interval)
        {
          sampledAt = Stopwatch.GetTimestamp();
          sample = SampleAsync(engines, id, Interlocked.Read(ref Revision), stopping);
        }
        return sample;
      }
    }

    static async Task<Sample> SampleAsync(EngineHost engines, string id, long revision, CancellationToken stopping)
    {
      try
      {
        using var lease = await engines.AcquireAsync(stopping).ConfigureAwait(false);
        var engine = lease.Engine;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, engine.TimeZone));
        var version = new ReportVersion(lease.Generation, revision, day, engine.GetStatistics(includePartitions: false).IndexMaintenance);
        try
        {
          var visitors = await engine.Web.GetCurrentVisitorsAsync(id, cancellationToken: stopping).ConfigureAwait(false);
          return new Sample(visitors, version);
        }
        catch (AnalyticsQueryTimeoutException)
        {
          return new Sample(null, version);
        }
      }
      catch (EngineUnavailableException)
      {
        return new Sample(null, null);
      }
    }
  }
}
