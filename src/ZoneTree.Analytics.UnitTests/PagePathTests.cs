using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

public sealed class PagePathTests
{
  [TestCase(null, null)]
  [TestCase("", "")]
  [TestCase("/docs/already-应用", "/docs/already-应用")]
  [TestCase("/Blog/net-%E5%BA%94%E7%94%A8", "/Blog/net-应用")]
  [TestCase("/caf%c3%a9/%F0%9F%8C%8D", "/café/🌍")]
  [TestCase("/caf%C3%A9%2F%3F%23%25%5C%3C%0A%252F+%20", "/café%2F%3F%23%25%5C%3C%0A%252F+%20")]
  [TestCase("/caf%C3%A9?q=%E5%BA%94#%C3%A9", "/café?q=%E5%BA%94#%C3%A9")]
  [TestCase("/ascii?q=%C3%A9", "/ascii?q=%C3%A9")]
  [TestCase("/caf%C3%A9#%C3%A9", "/café#%C3%A9")]
  [TestCase("/%C0%AF/%E0%80%AF/%ED%A0%80/%F4%90%80%80/%E2%98/%ZZ/%/%C3%A9", "/%C0%AF/%E0%80%AF/%ED%A0%80/%F4%90%80%80/%E2%98/%ZZ/%/é")]
  [TestCase("/%c2%85/%e2%80%ae%E2%81%A6%E2%80%A8%C3%A9", "/%c2%85/%e2%80%ae%E2%81%A6%E2%80%A8é")]
  [TestCase("/a\u202Eb\u0000?x=\u0085#\u2029", "/a%E2%80%AEb%00?x=%C2%85#%E2%80%A9")]
  public void FormatsLabelsWithoutChangingPathStructure(string path, string label)
  {
    var formatted = PagePath.FormatForDisplay(path);
    Assert.Multiple(() =>
    {
      Assert.That(formatted, Is.EqualTo(label));
      Assert.That(PagePath.FormatForDisplay(formatted), Is.EqualTo(label));
    });
  }
}
