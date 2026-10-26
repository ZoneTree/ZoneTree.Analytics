namespace ZoneTree.Analytics.UnitTests;

public sealed class PureStackLogGroupTests
{
  static List<string> Group(params string[] lines)
  {
    var written = new List<string>();
    using (var group = new PureStackLogGroup(written.Add))
      foreach (var line in lines)
        group.Add(line);
    return written;
  }

  [Test]
  public void EachMessageAndItsDetailsBecomeOneEntry()
  {
    var written = Group(
        "ℹ️ INFO 17:54:25,632 static assets discovered",
        "{",
        "  \"assets\": []",
        "}",
        "ℹ️ INFO 17:54:25,632 serving at",
        "{",
        "  \"url\": \"http://127.0.0.1:15447/\"",
        "}");
    Assert.That(written, Is.EqualTo(new[]
    {
      "ℹ️ INFO 17:54:25,632 static assets discovered { \"assets\": [] }",
      "ℹ️ INFO 17:54:25,632 serving at { \"url\": \"http://127.0.0.1:15447/\" }",
    }));
  }

  [Test]
  public void NestedDetailsAndBracesInsideStringsKeepTheirMessageTogether()
  {
    var written = Group(
        "ℹ️ INFO 10:00:00,000 page rendered",
        "{",
        "  \"route\": \"/blog/{slug}/\",",
        "  \"timing\": {",
        "    \"ms\": 12",
        "  }",
        "}");
    Assert.That(written, Is.EqualTo(new[] { "ℹ️ INFO 10:00:00,000 page rendered { \"route\": \"/blog/{slug}/\", \"timing\": { \"ms\": 12 } }" }));
  }

  [Test]
  public void MessagesWithoutDetailsAreWrittenOnTheirOwn()
  {
    var written = Group(
        "ℹ️ INFO 10:00:00,000 watching for changes",
        "ℹ️ INFO 10:00:01,000 build completed",
        "{",
        "  \"pages\": 20",
        "}",
        "ℹ️ INFO 10:00:02,000 stopped");
    Assert.That(written, Is.EqualTo(new[]
    {
      "ℹ️ INFO 10:00:00,000 watching for changes",
      "ℹ️ INFO 10:00:01,000 build completed { \"pages\": 20 }",
      "ℹ️ INFO 10:00:02,000 stopped",
    }));
  }

  [Test]
  public void ColorsAreKeptAndIgnoredWhenGrouping()
  {
    const string green = "\u001b[38;2;52;150;52m", blue = "\u001b[38;2;111;184;214m", reset = "\u001b[39m";
    var written = Group(
        $"ℹ️ {green}INFO {blue}18:01:53,089 {green}static assets discovered{reset}",
        $"{green}{{{reset}",
        $"  \"assets\"{green}:{reset} {green}[]{reset}",
        $"{green}}}{reset}");
    Assert.That(written, Is.EqualTo(new[]
    {
      $"ℹ️ {green}INFO {blue}18:01:53,089 {green}static assets discovered{reset} {green}{{{reset} \"assets\"{green}:{reset} {green}[]{reset} {green}}}{reset}",
    }));
    Assert.That(PureStackLogGroup.Visible(written[0]), Is.EqualTo("ℹ️ INFO 18:01:53,089 static assets discovered { \"assets\": [] }"));
  }

  [Test]
  public void AMessageWithNothingAfterItIsWrittenAfterAPause()
  {
    var written = new List<string>();
    using var group = new PureStackLogGroup(text => { lock (written) written.Add(text); });
    group.Add("ℹ️ INFO 10:00:00,000 file changed, rebuilding");
    Assert.That(() => { lock (written) return written.Count; }, Is.EqualTo(1).After(3000, 50));
    Assert.That(written[0], Is.EqualTo("ℹ️ INFO 10:00:00,000 file changed, rebuilding"));
  }
}
