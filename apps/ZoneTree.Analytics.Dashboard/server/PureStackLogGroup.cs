using System.Text.RegularExpressions;

// PureStack prints a message line, then its details as indented JSON. Lines are held until every
// '{' is closed again, so each message becomes one log entry with its details on the same line.
// A message without details is written when the next line does not open its JSON, or after a
// short pause when nothing follows. PureStack's colors are kept: the decisions look past them.
sealed partial class PureStackLogGroup : IDisposable
{
  [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
  private static partial Regex ColorCode();

  static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

  readonly Action<string> write;
  readonly object sync = new();
  readonly List<string> lines = [];
  readonly Timer idle;
  int depth;
  bool opened;

  public PureStackLogGroup(Action<string> write)
  {
    this.write = write;
    idle = new Timer(_ => { lock (sync) Flush(); });
  }

  // The text without its color codes, for decisions about it.
  public static string Visible(string text) => ColorCode().Replace(text, "");

  public void Add(string line)
  {
    var visible = Visible(line);
    lock (sync)
    {
      // A message without details ends where a line at the top level does not open JSON.
      if (depth == 0 && lines.Count > 0 && !visible.TrimStart().StartsWith('{')) Flush();
      lines.Add(line);
      Count(visible);
      if (opened && depth == 0) Flush();
      else idle.Change(Pause, Timeout.InfiniteTimeSpan);
    }
  }

  // Braces inside JSON strings, such as "{slug}", do not count.
  void Count(string visible)
  {
    var inString = false;
    var escaped = false;
    foreach (var c in visible)
    {
      if (inString)
      {
        if (escaped) escaped = false;
        else if (c == '\\') escaped = true;
        else if (c == '"') inString = false;
        continue;
      }
      switch (c)
      {
        case '"' when depth > 0: inString = true; break;
        case '{': depth++; opened = true; break;
        case '}' when depth > 0: depth--; break;
      }
    }
  }

  void Flush()
  {
    if (lines.Count == 0) return;
    var text = string.Join(' ', lines.Select(line => line.Trim()));
    lines.Clear();
    depth = 0;
    opened = false;
    write(text);
  }

  public void Dispose()
  {
    idle.Dispose();
    lock (sync) Flush();
  }
}
