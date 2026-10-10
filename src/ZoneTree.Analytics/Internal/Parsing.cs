namespace ZoneTree.Analytics.Internal;

/// <summary>
/// The one error model of the text syntax of query types: a null text is an
/// <see cref="ArgumentNullException"/>, any text that is not valid is a <see cref="FormatException"/>,
/// and TryParse returns false for both.
/// </summary>
static class Parsing
{
  public static T Parse<T>(string text, string what, Func<string, T> parse)
  {
    ArgumentNullException.ThrowIfNull(text);
    if (string.IsNullOrWhiteSpace(text))
      throw new FormatException($"An empty text is not a valid {what}.");
    try
    {
      return parse(text.Trim());
    }
    catch (ArgumentException e)
    {
      throw new FormatException($"'{text}' is not a valid {what}: {e.Message}", e);
    }
  }

  public static bool TryParse<T>(string text, Func<string, T> parse, out T result)
  {
    result = default;
    if (string.IsNullOrWhiteSpace(text))
      return false;
    try
    {
      result = parse(text);
      return true;
    }
    catch (FormatException)
    {
      return false;
    }
  }
}
