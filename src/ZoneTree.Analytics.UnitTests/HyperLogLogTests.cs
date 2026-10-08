using ZoneTree.Analytics.Sketches;

namespace ZoneTree.Analytics.UnitTests;

public sealed class HyperLogLogTests
{
  [Test]
  public void SmallSetsAreExact()
  {
    var sketch = new HyperLogLog();
    for (ulong i = 1; i <= 4000; ++i)
    {
      sketch.Add(i);
      sketch.Add(i); // duplicates are ignored
    }
    Assert.Multiple(() =>
    {
      Assert.That(sketch.IsSparse, Is.True);
      Assert.That(sketch.Estimate(), Is.EqualTo(4000));
    });
  }

  [TestCase(10_000)]
  [TestCase(100_000)]
  [TestCase(1_000_000)]
  public void LargeSetsAreAccurate(int count)
  {
    var sketch = new HyperLogLog();
    for (var i = 1; i <= count; ++i)
      sketch.Add((ulong)i);
    var error = Math.Abs(sketch.Estimate() - count) / (double)count;
    Assert.Multiple(() =>
    {
      Assert.That(sketch.IsSparse, Is.False);
      // 0.81% standard error; allow 4 sigma.
      Assert.That(error, Is.LessThan(0.033));
    });
  }

  [Test]
  public void UnionDeduplicatesAcrossSketches()
  {
    var a = new HyperLogLog();
    var b = new HyperLogLog();
    for (ulong i = 1; i <= 1500; ++i)
      a.Add(i);
    for (ulong i = 1001; i <= 2500; ++i)
      b.Add(i);
    var union = a.Clone();
    union.UnionWith(b);
    Assert.That(union.Estimate(), Is.EqualTo(2500).Within(2500 * 0.03));

    var smallA = new HyperLogLog();
    var smallB = new HyperLogLog();
    for (ulong i = 1; i <= 100; ++i)
      smallA.Add(i);
    for (ulong i = 51; i <= 150; ++i)
      smallB.Add(i);
    smallA.UnionWith(smallB);
    Assert.That(smallA.Estimate(), Is.EqualTo(150));
  }

  [Test]
  public void UnionOfSparseAndDense()
  {
    var dense = new HyperLogLog();
    for (ulong i = 1; i <= 50_000; ++i)
      dense.Add(i);
    var sparse = new HyperLogLog();
    for (ulong i = 50_001; i <= 50_100; ++i)
      sparse.Add(i);
    var left = dense.Clone();
    left.UnionWith(sparse);
    var right = sparse.Clone();
    right.UnionWith(dense);
    Assert.That(left.Estimate(), Is.EqualTo(right.Estimate()));
    Assert.That(left.Estimate(), Is.EqualTo(50_100).Within(50_100 * 0.033));
  }

  [Test]
  public void CloneIsIndependent()
  {
    var original = new HyperLogLog();
    original.Add(1);
    var clone = original.Clone();
    clone.Add(2);
    Assert.Multiple(() =>
    {
      Assert.That(original.Estimate(), Is.EqualTo(1));
      Assert.That(clone.Estimate(), Is.EqualTo(2));
    });
  }

  [Test]
  public void SerializationRoundTrips()
  {
    foreach (var count in new[] { 0, 1, 500, 100_000 })
    {
      var sketch = new HyperLogLog(12);
      for (var i = 1; i <= count; ++i)
        sketch.Add((ulong)i * 7919);
      var restored = HyperLogLog.FromByteArray(sketch.ToByteArray());
      Assert.Multiple(() =>
      {
        Assert.That(restored.Precision, Is.EqualTo(12));
        Assert.That(restored.Estimate(), Is.EqualTo(sketch.Estimate()));
        Assert.That(restored.IsSparse, Is.EqualTo(sketch.IsSparse));
      });
    }
  }

  [Test]
  public void DifferentPrecisionsCannotBeMerged()
  {
    var a = new HyperLogLog(12);
    var b = new HyperLogLog(14);
    b.Add(1);
    Assert.Throws<InvalidOperationException>(() => a.UnionWith(b));
  }
}
