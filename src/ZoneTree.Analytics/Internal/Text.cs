namespace ZoneTree.Analytics.Internal;

/// <summary>
/// Sanitization of names and values before they are stored.
/// </summary>
static class Text
{
  /// <summary>
  /// The maximum length of event, site, dimension and metric names.
  /// </summary>
  public const int MaxNameLength = 128;

  /// <summary>
  /// Sanitizes a name (event, site, dimension or metric name): removes control characters,
  /// trims and truncates. Returns empty for null.
  /// </summary>
  public static string Name(string value)
  {
    if (string.IsNullOrEmpty(value))
      return "";
    if (ContainsControl(value))
      value = string.Concat(value.Where(c => !char.IsControl(c)));
    return Truncate(value.Trim(), MaxNameLength);
  }

  /// <summary>
  /// Sanitizes a dimension value: replaces control characters with spaces,
  /// trims and truncates. Returns empty for null.
  /// </summary>
  public static string Value(string value, int maxLength)
  {
    if (string.IsNullOrEmpty(value))
      return "";
    if (ContainsControl(value))
    {
      value = string.Create(value.Length, value, (span, source) =>
      {
        for (var i = 0; i < source.Length; ++i)
          span[i] = char.IsControl(source[i]) ? ' ' : source[i];
      });
    }
    return Truncate(value.Trim(), maxLength);
  }

  static bool ContainsControl(string value)
  {
    foreach (var c in value)
    {
      if (char.IsControl(c))
        return true;
    }
    return false;
  }

  static string Truncate(string value, int maxLength)
  {
    if (value.Length <= maxLength)
      return value;
    var length = maxLength;
    if (char.IsHighSurrogate(value[length - 1]))
      --length;
    return value[..length];
  }
}
