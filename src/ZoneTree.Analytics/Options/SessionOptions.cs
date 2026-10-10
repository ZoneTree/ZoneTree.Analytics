using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics;

/// <summary>
/// Session tracking options.
/// </summary>
public sealed class SessionOptions
{
  /// <summary>
  /// Enables sessions, bounce rate, session duration, entry and exit pages.
  /// Default is true.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Inactivity timeout that ends a session. Default is 30 minutes.
  /// </summary>
  public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);

  /// <summary>
  /// Maximum number of sessions cached in memory per worker. Evicted sessions
  /// remain available in storage. Default is 250,000.
  /// </summary>
  public int MaxCachedSessions { get; set; } = 250_000;

  /// <summary>
  /// Custom dimensions that are session-level, for example an experiment variant or a plan.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Like source, country or device, a session-level dimension is captured from the first event
  /// of a session and attached to every event of that session; values on later events of the
  /// session are ignored. Session metrics (sessions, bounce rate, duration) and entrances and
  /// exits can then be grouped and filtered by it, and enrichers can set it on the first event.
  /// </para>
  /// <para>
  /// Track the value as an event dimension, for example
  /// <c>Dimensions = { ["ab_variant"] = "B" }</c>, and add a <see cref="RollupDefinition"/> for it
  /// to answer its reports from rollups. Dimensions can be added or removed at any time;
  /// a dimension added later applies to sessions that start afterwards.
  /// </para>
  /// </remarks>
  public List<string> Dimensions { get; private set; } = [];

  /// <summary>
  /// At most this many custom session-level dimensions are allowed.
  /// </summary>
  internal const int MaxDimensions = 16;

  internal SessionOptions Clone()
  {
    var clone = (SessionOptions)MemberwiseClone();
    clone.Dimensions = Dimensions == null ? [] : [.. Dimensions];
    return clone;
  }

  internal void Validate(PrivacyOptions privacy)
  {
    if (Timeout <= TimeSpan.Zero)
      throw new ArgumentException("Sessions.Timeout must be positive.");
    if (MaxCachedSessions < 1)
      throw new ArgumentException("Sessions.MaxCachedSessions must be positive.");
    if (Dimensions.Count > MaxDimensions)
      throw new ArgumentException($"Sessions.Dimensions can hold at most {MaxDimensions} dimensions.");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var name in Dimensions)
    {
      if (string.IsNullOrWhiteSpace(name) || Text.Name(name) != name ||
          name.Contains(',') || name[0] == '$')
        throw new ArgumentException($"Sessions.Dimensions: '{name}' is not a valid dimension name.");
      if (StandardDimensions.SessionLevel.Contains(name))
        throw new ArgumentException($"Sessions.Dimensions: '{name}' is already a standard session-level dimension.");
      if (name is StandardDimensions.Page or StandardDimensions.Hostname or StandardDimensions.EntryPage ||
          StandardDimensions.BotLevel.Contains(name))
        throw new ArgumentException($"Sessions.Dimensions: '{name}' is maintained by the engine and cannot be a session dimension.");
      if (!seen.Add(name))
        throw new ArgumentException($"Sessions.Dimensions contains '{name}' twice.");
      if (!privacy.IsCustomDimensionAllowed(name))
        throw new ArgumentException(
            $"Sessions.Dimensions: '{name}' is excluded by Privacy.AllowedDimensions or Privacy.DeniedDimensions.");
    }
  }
}
