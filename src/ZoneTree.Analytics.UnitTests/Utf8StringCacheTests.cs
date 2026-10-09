using System.Text;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.UnitTests;

public sealed class Utf8StringCacheTests
{
  [Test]
  public void RepeatedAsciiValuesReturnTheCachedString()
  {
    var cache = new Utf8StringCache();
    var first = cache.Get("/pricing"u8);
    var second = cache.Get("/pricing"u8);
    Assert.Multiple(() =>
    {
      Assert.That(first, Is.EqualTo("/pricing"));
      Assert.That(second, Is.SameAs(first));
    });
  }

  [Test]
  public void EveryValueDecodesCorrectly()
  {
    var cache = new Utf8StringCache();
    string[] values = ["", "a", "Türkiye", "日本", new string('x', 500), "/blog/ü", "ÿ"];
    // Many distinct values evict each other's slots; results must stay correct.
    var many = Enumerable.Range(0, 20_000).Select(i => "/page/" + i).ToArray();
    for (var round = 0; round < 2; ++round)
    {
      foreach (var value in values.Concat(many))
        Assert.That(cache.Get(Encoding.UTF8.GetBytes(value)), Is.EqualTo(value));
    }
  }
}
