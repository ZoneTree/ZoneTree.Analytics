namespace ZoneTree.Analytics.Ingestion;

/// <summary>
/// The session-level dimensions of an engine: the standard ones of
/// <see cref="StandardDimensions.SessionLevel"/> at fixed positions, followed by the custom ones
/// of <see cref="SessionOptions.Dimensions"/>. Session-level values are captured from the first
/// event of a session and attached to every event of the session.
/// </summary>
sealed class SessionSchema
{
  readonly Dictionary<string, int> indexes;

  public SessionSchema(IEnumerable<string> customDimensions)
  {
    Names = [.. StandardDimensions.SessionLevel, .. customDimensions ?? []];
    indexes = Names
        .Select((name, index) => (name, index))
        .ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);
  }

  /// <summary>The schema without custom session-level dimensions.</summary>
  public static SessionSchema Standard { get; } = new([]);

  /// <summary>Standard names first, then custom names.</summary>
  public IReadOnlyList<string> Names { get; }

  public int Count => Names.Count;

  public static int StandardCount => StandardDimensions.SessionLevel.Count;

  public int IndexOf(string dimension) =>
      dimension != null && indexes.TryGetValue(dimension, out var index) ? index : -1;

  public bool IsSessionLevel(string dimension) => dimension != null && indexes.ContainsKey(dimension);

  /// <summary>
  /// True when rows of the scope can carry the dimension.
  /// </summary>
  public bool Contains(DimensionScope scope, string dimension) => scope switch
  {
    DimensionScope.Session =>
        dimension == StandardDimensions.EntryPage || IsSessionLevel(dimension),
    DimensionScope.PageView =>
        dimension is StandardDimensions.EntryPage or StandardDimensions.Page or StandardDimensions.Hostname ||
        IsSessionLevel(dimension),
    DimensionScope.Bot => StandardDimensions.BotLevel.Contains(dimension),
    _ => true
  };

  /// <summary>
  /// True when every dimension can be carried by rows of the scope.
  /// </summary>
  public bool ContainsAll(DimensionScope scope, IEnumerable<string> dimensions) =>
      dimensions.All(x => Contains(scope, x));

  /// <summary>
  /// True when contributions of the scope can reach the rollup. Engine-generated rows
  /// (sessions, entrances and exits, bots) only reach rollups over dimensions they carry,
  /// unless the rollup explicitly lists their event.
  /// </summary>
  public bool Accepts(RollupDefinition definition, DimensionScope scope, string eventName) => scope switch
  {
    DimensionScope.All => true,
    DimensionScope.Bot => ContainsAll(scope, definition.Dimensions) ||
        (definition.Events != null && definition.Events.Contains(eventName)),
    _ => ContainsAll(scope, definition.Dimensions)
  };
}
