// Prints the setup link below everything else the host prints at startup, so it is the last
// thing in the console. It is written directly to the console: the logger writes from a
// background queue, and PureStack keeps logging its first render after the host has started.
static class SetupBanner
{
  static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);
  static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

  public static async Task ShowAsync(string dashboardUrl, SetupKey key, SetupStatus setup,
      DashboardDevelopmentServer developmentServer, CancellationToken stopping)
  {
    try
    {
      var deadline = DateTime.UtcNow + MaxWait;
      do
        await Task.Delay(TimeSpan.FromMilliseconds(500), stopping);
      while (developmentServer != null && DateTime.UtcNow < deadline && DateTime.UtcNow - developmentServer.LastOutputUtc < Quiet);
    }
    catch (OperationCanceledException)
    {
      return;
    }
    var value = key.Value;
    if (setup.Complete || value == null) return;
    var link = $"{dashboardUrl.TrimEnd('/')}/setup/?key={value}";
    string[] lines =
    [
      "Finish setting up ZoneTree Analytics:",
      "",
      $"  {link}",
      "",
      $"Setup key: {value}",
      "The key changes on every start and stops working once setup is complete.",
    ];
    var width = lines.Max(line => line.Length) + 4;
    var border = "+" + new string('-', width) + "+";
    var output = Console.Out;
    var colored = !Console.IsOutputRedirected;
    lock (output)
    {
      output.WriteLine();
      if (colored) Console.ForegroundColor = ConsoleColor.Cyan;
      output.WriteLine(border);
      foreach (var line in lines)
        output.WriteLine("|  " + line.PadRight(width - 2) + "|");
      output.WriteLine(border);
      if (colored) Console.ResetColor();
      output.WriteLine();
      output.Flush();
    }
  }
}
