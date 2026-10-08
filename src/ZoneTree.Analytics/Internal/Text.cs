namespace ZoneTree.Analytics.Internal;

static class Text
{
  /// <summary>
  /// Sanitizes a name (event, site, dimension or metric name): trims, removes control
  /// characters and truncates. Returns empty for null.
  /// </summary>
  public static string Name(string value, int maxLength)
  {
    if (string.IsNullOrEmpty(value))
      return "";
    var needsCleaning = false;
    foreach (var c in value)
    {
      if (char.IsControl(c))
      {
        needsCleaning = true;
        break;
      }
    }
    if (needsCleaning)
      value = string.Concat(value.Where(c => !char.IsControl(c)));
    value = value.Trim();
    return Truncate(value, maxLength);
  }

  /// <summary>
  /// Sanitizes a dimension value: replaces control characters with spaces,
  /// trims and truncates. Returns empty for null.
  /// </summary>
  public static string Value(string value, int maxLength)
  {
    if (string.IsNullOrEmpty(value))
      return "";
    var needsCleaning = false;
    foreach (var c in value)
    {
      if (char.IsControl(c))
      {
        needsCleaning = true;
        break;
      }
    }
    if (needsCleaning)
      value = string.Create(value.Length, value, (span, source) =>
      {
        for (var i = 0; i < source.Length; ++i)
          span[i] = char.IsControl(source[i]) ? ' ' : source[i];
      });
    value = value.Trim();
    return Truncate(value, maxLength);
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
