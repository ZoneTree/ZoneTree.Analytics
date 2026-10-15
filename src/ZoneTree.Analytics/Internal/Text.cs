using System.Text;

namespace ZoneTree.Analytics.Internal;

/// <summary>
/// Sanitization of names and values before they are stored.
/// </summary>
static class Text
{
  static readonly UTF8Encoding StrictUtf8 = new(false, true);

  /// <summary>Rejects names that cannot be represented unambiguously in storage keys.</summary>
  public static void ValidateStoredName(string value, string parameter)
  {
    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
      throw new ArgumentException("Stored names must be non-empty and contain no control characters.", parameter);
    ValidateUnicode(value, parameter);
  }

  /// <summary>Rejects malformed UTF-16 before UTF-8 encoding can replace it.</summary>
  public static void ValidateUnicode(string value, string parameter)
  {
    if (value == null)
      return;
    try
    {
      StrictUtf8.GetByteCount(value);
    }
    catch (EncoderFallbackException exception)
    {
      throw new ArgumentException("Text must contain valid Unicode.", parameter, exception);
    }
  }

  /// <summary>
  /// The maximum length of event, site, dimension and value names.
  /// </summary>
  public const int MaxNameLength = 128;

  /// <summary>
  /// Sanitizes a name (event, site, dimension or value name): removes control characters,
  /// replaces unpaired surrogates, trims and truncates. Returns empty for null.
  /// </summary>
  public static string Name(string value)
  {
    if (string.IsNullOrEmpty(value))
      return "";
    if (ContainsControl(value))
      value = string.Concat(value.Where(c => !char.IsControl(c)));
    return Truncate(ReplaceUnpairedSurrogates(value).Trim(), MaxNameLength);
  }

  /// <summary>
  /// Replaces each unpaired surrogate with U+FFFD, as UTF-8 encoding does when the text is
  /// stored. Sanitized text then equals the text read back, so its hashes and in-memory keys
  /// agree with the stored bytes.
  /// </summary>
  static string ReplaceUnpairedSurrogates(string value)
  {
    var first = value.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF');
    if (first < 0)
      return value;
    var index = IndexOfUnpairedSurrogate(value, first);
    if (index < 0)
      return value;
    var chars = value.ToCharArray();
    for (; index >= 0; index = IndexOfUnpairedSurrogate(value, index + 1))
      chars[index] = '�';
    return new string(chars);
  }

  static int IndexOfUnpairedSurrogate(string value, int start)
  {
    for (var i = start; i < value.Length; ++i)
    {
      var c = value[i];
      if (!char.IsSurrogate(c))
        continue;
      if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
      {
        ++i;
        continue;
      }
      return i;
    }
    return -1;
  }

  /// <summary>
  /// A stable 64-bit hash of a dimension value (FNV-1a over UTF-16 code units), for distinct
  /// value sketches. Rollups persist sketches of it: it must never change.
  /// </summary>
  public static ulong StableHash(string value)
  {
    var hash = 14695981039346656037UL;
    foreach (var c in value)
    {
      hash ^= c;
      hash *= 1099511628211UL;
    }
    return hash;
  }

  /// <summary>
  /// Sanitizes a dimension value: replaces control characters with spaces and unpaired
  /// surrogates with U+FFFD, trims and truncates. Returns empty for null.
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
    return Truncate(ReplaceUnpairedSurrogates(value).Trim(), maxLength);
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
