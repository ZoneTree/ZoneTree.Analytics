using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZoneTree.Analytics.Web;

/// <summary>Formats page paths for report labels.</summary>
public static partial class PagePath
{
  /// <summary>
  /// Decodes UTF-8 escapes for non-ASCII characters in the path. ASCII escapes, malformed
  /// sequences and the encoding of any query string or fragment are preserved. Control and
  /// bidirectional formatting characters are shown as escapes.
  /// </summary>
  /// <remarks>Render the result as plain text. Use the original page value as the report key.</remarks>
  public static string FormatForDisplay(string path)
  {
    if (string.IsNullOrEmpty(path))
      return path;

    var firstEscape = path.IndexOf('%');
    if (firstEscape >= 0)
    {
      var suffix = path.AsSpan().IndexOfAny('?', '#');
      if (suffix < 0)
        path = Utf8Escapes().Replace(path, DecodeCharacter);
      else if (firstEscape < suffix)
        path = Utf8Escapes().Replace(path[..suffix], DecodeCharacter) + path[suffix..];
    }

    return UnsafeLabelCharacters().Replace(path, static match => Uri.EscapeDataString(match.Value));
  }

  static string DecodeCharacter(Match match)
  {
    var encoded = match.ValueSpan;
    Span<byte> bytes = stackalloc byte[encoded.Length / 3];
    for (var i = 0; i < bytes.Length; i++)
      bytes[i] = byte.Parse(encoded.Slice(i * 3 + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    if (Rune.DecodeFromUtf8(bytes, out var rune, out _) != OperationStatus.Done)
      return match.Value;
    var character = rune.ToString();
    return UnsafeLabelCharacters().IsMatch(character) ? match.Value : character;
  }

  // Match one potential UTF-8 character so malformed escapes don't hide later valid characters.
  [GeneratedRegex("%[cd][0-9a-f]%[89ab][0-9a-f]|%e[0-9a-f](?:%[89ab][0-9a-f]){2}|%f[0-4](?:%[89ab][0-9a-f]){3}",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex Utf8Escapes();

  // Unicode Bidi_Control plus control characters and line/paragraph separators.
  [GeneratedRegex(@"[\p{Cc}\u061C\u200E\u200F\u2028-\u202E\u2066-\u2069]")]
  private static partial Regex UnsafeLabelCharacters();
}
