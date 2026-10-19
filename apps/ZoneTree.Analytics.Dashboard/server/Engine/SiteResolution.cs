// Chooses the site of a tracker beacon from the page that sent it: the Origin header, else the
// Referer, matched against each site's domains. Null means the event is not recorded.
sealed class SiteResolution
{
  sealed record Wildcard(string Suffix, string Apex, string Site, bool WithPort);

  sealed record Lookup(DashboardSettings Settings, Dictionary<string, string> Exact, List<Wildcard> Wildcards);

  Lookup lookup;

  public string Resolve(HttpContext context, DashboardSettings settings)
  {
    var current = Volatile.Read(ref lookup);
    if (current == null || !ReferenceEquals(current.Settings, settings))
      Volatile.Write(ref lookup, current = Build(settings));
    var host = SourceHost(context);
    if (host == null) return Unmatched(settings);
    if (string.Equals(host, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
      return settings.Sites.TrackDashboardUsage ? SettingsMapper.DashboardUsageSite : null;
    var name = host.Split(':')[0];
    if (current.Exact.TryGetValue(host, out var site) || current.Exact.TryGetValue(name, out site)) return site;
    foreach (var wildcard in current.Wildcards)
    {
      // A pattern with a port matches only pages served on that port.
      var candidate = wildcard.WithPort ? host : name;
      if (candidate.EndsWith(wildcard.Suffix, StringComparison.OrdinalIgnoreCase)
          || string.Equals(candidate, wildcard.Apex, StringComparison.OrdinalIgnoreCase))
        return wildcard.Site;
    }
    return Unmatched(settings);
  }

  static string Unmatched(DashboardSettings settings) =>
      settings.Sites.UnmatchedTraffic == UnmatchedTraffic.PrimarySite ? settings.Sites.PrimarySite : null;

  static string SourceHost(HttpContext context)
  {
    foreach (var header in new[] { context.Request.Headers.Origin.ToString(), context.Request.Headers.Referer.ToString() })
      if (!string.IsNullOrEmpty(header) && header != "null" && Uri.TryCreate(header, UriKind.Absolute, out var uri)
          && uri.Scheme is "http" or "https")
        return uri.IsDefaultPort ? uri.Host : uri.Authority;
    return null;
  }

  static Lookup Build(DashboardSettings settings)
  {
    var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var wildcards = new List<Wildcard>();
    foreach (var site in settings.Sites.Items)
      foreach (var domain in site.Domains)
      {
        // *.example.com covers example.com and every subdomain.
        if (domain.StartsWith("*.", StringComparison.Ordinal)) wildcards.Add(new(domain[1..], domain[2..], site.Id, domain.Contains(':')));
        else exact[domain] = site.Id;
      }
    // The longest suffix wins: *.shop.example.com before *.example.com.
    wildcards.Sort((first, second) => second.Suffix.Length.CompareTo(first.Suffix.Length));
    return new(settings, exact, wildcards);
  }
}
