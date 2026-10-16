using System.Runtime.InteropServices;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// The rollup value layout: scalars first, then length-prefixed sketch sections that are decoded
/// only when read, and readers that reject what they do not understand.
/// </summary>
public sealed class RollupValueLayoutTests
{
  static readonly RollupValueSerializer Serializer = new();

  /// <summary>A row with a dense visitor sketch, percentiles and a distinct-value sketch.</summary>
  static RollupValue HeavyRow(int visitors = 5_000)
  {
    var accumulator = new RollupAccumulator { Count = visitors };
    for (var i = 0; i < visitors; ++i)
    {
      accumulator.AddVisitor((ulong)i + 1, HyperLogLog.DefaultPrecision);
      accumulator.AddValue("load_time", i);
      accumulator.AddValueDetail("load_time", i, 1000 + i, ValueAggregates.All, QuantileSketch.BucketOf(i));
      accumulator.AddDistinct("page", Text.StableHash("/p" + i), HyperLogLog.DefaultPrecision);
    }
    return accumulator.ToValue();
  }

  static long AllocatedPerCall(Action action, int iterations = 200)
  {
    action();
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < iterations; ++i)
      action();
    return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
  }

  [Test]
  public void QueriesDecodeOnlyTheSketchesTheyRead()
  {
    var bytes = Serializer.Serialize(HeavyRow());
    var scalarsOnly = new RollupReadRequirements(false, null, []);
    // Before: every read decoded the 16 KiB visitor sketch, the percentiles and the distinct sketch.
    var allocated = AllocatedPerCall(() => new RollupAccumulator().MergeForQuery(Serializer.Deserialize(bytes), scalarsOnly));
    Assert.That(allocated, Is.LessThan(1_024), $"a scalar read allocated {allocated:N0} bytes");

    var group = new RollupAccumulator();
    group.MergeForQuery(Serializer.Deserialize(bytes), new RollupReadRequirements(
        true, new() { ["load_time"] = ValueAggregates.All }, ["page"]));
    Assert.Multiple(() =>
    {
      Assert.That(group.Count, Is.EqualTo(5_000));
      Assert.That(group.HasData, Is.True);
      Assert.That(group.Visitors.Estimate(), Is.EqualTo(5_000).Within(5_000 * 0.03));
      Assert.That(group.DistinctOf("page").Estimate(), Is.EqualTo(5_000).Within(5_000 * 0.03));
      Assert.That(group.DetailOf("load_time").Quantiles.Count, Is.EqualTo(5_000));
      Assert.That(group.DetailOf("load_time").First, Is.Zero);
      Assert.That(group.DetailOf("load_time").Last, Is.EqualTo(4_999));
    });
  }

  static readonly string[] DistinctDimensions = ["browser", "country", "page", "source"];

  static readonly string[] AggregatedValues = ["a", "b", "c"];

  /// <summary>A row with dense distinct sketches of four dimensions and percentiles of three values.</summary>
  static RollupValue WideRow()
  {
    var accumulator = new RollupAccumulator { Count = 5_000 };
    for (var i = 0; i < 5_000; ++i)
    {
      foreach (var value in AggregatedValues)
      {
        accumulator.AddValue(value, i);
        accumulator.AddValueDetail(value, i, 1000 + i, ValueAggregates.Percentiles, QuantileSketch.BucketOf(i));
      }
      foreach (var dimension in DistinctDimensions)
        accumulator.AddDistinct(dimension, Text.StableHash(dimension + i), HyperLogLog.DefaultPrecision);
    }
    return accumulator.ToValue();
  }

  [Test]
  public void QueriesDecodeOnlyTheDistinctSketchAndValueDetailTheyRead()
  {
    var bytes = Serializer.Serialize(WideRow());
    long Read(RollupReadRequirements requirements) =>
        AllocatedPerCall(() => new RollupAccumulator().MergeForQuery(Serializer.Deserialize(bytes), requirements), 50);
    var oneDistinct = Read(new(false, null, ["page"]));
    var allDistinct = Read(new(false, null, DistinctDimensions));
    var oneDetail = Read(new(false, new() { ["b"] = ValueAggregates.Percentiles }, []));
    var allDetails = Read(new(false, AggregatedValues.ToDictionary(x => x, _ => ValueAggregates.Percentiles), []));
    TestContext.Out.WriteLine($"distinct: one {oneDistinct:N0} B, all {allDistinct:N0} B; details: one {oneDetail:N0} B, all {allDetails:N0} B");
    Assert.Multiple(() =>
    {
      // Before: reading one entry decoded every sketch of its section.
      Assert.That(oneDistinct, Is.LessThan(allDistinct / 3), $"one distinct sketch {oneDistinct:N0} B, all {allDistinct:N0} B");
      Assert.That(oneDetail, Is.LessThan(allDetails / 2), $"one value detail {oneDetail:N0} B, all {allDetails:N0} B");
    });

    var group = new RollupAccumulator();
    group.MergeForQuery(Serializer.Deserialize(bytes), new RollupReadRequirements(
        false, new() { ["b"] = ValueAggregates.Percentiles }, ["page"]));
    Assert.Multiple(() =>
    {
      Assert.That(group.DistinctOf("page").Estimate(), Is.EqualTo(5_000).Within(5_000 * 0.03));
      Assert.That(group.DistinctOf("country"), Is.Null);
      Assert.That(group.DetailOf("b").Quantiles.Count, Is.EqualTo(5_000));
      Assert.That(group.DetailOf("a"), Is.Null);
    });
  }

  [Test]
  public void MergesKeepUntouchedDetailsAndDistinctSketchesAsStoredBytes()
  {
    var bytes = Serializer.Serialize(WideRow()).ToArray();
    var stored = Serializer.Deserialize(bytes);
    // An ingestion delta that updates the percentiles of "b" and the distinct pages only.
    var delta = new RollupAccumulator { Count = 1 };
    delta.AddValue("b", 7);
    delta.AddValueDetail("b", 7, 99, ValueAggregates.Percentiles, QuantileSketch.BucketOf(7));
    delta.AddDistinct("page", Text.StableHash("/new"), HyperLogLog.DefaultPrecision);
    var merged = RollupValue.Merge(stored, delta.ToValue());
    Assert.Multiple(() =>
    {
      for (var i = 0; i < AggregatedValues.Length; ++i)
      {
        var updated = AggregatedValues[i] == "b";
        Assert.That(ReferenceEquals(merged.DetailEntryAt(i), stored.DetailEntryAt(i)), Is.EqualTo(!updated), AggregatedValues[i]);
        Assert.That(merged.DetailEntryAt(i).IsStored, Is.EqualTo(!updated), AggregatedValues[i]);
      }
      for (var i = 0; i < DistinctDimensions.Length; ++i)
      {
        var updated = DistinctDimensions[i] == "page";
        Assert.That(ReferenceEquals(merged.DistinctEntryAt(i), stored.DistinctEntryAt(i)), Is.EqualTo(!updated), DistinctDimensions[i]);
      }
    });

    var restored = Serializer.Deserialize(Serializer.Serialize(merged));
    var original = Serializer.Deserialize(bytes);
    Assert.Multiple(() =>
    {
      Assert.That(restored.DetailAt(1).Quantiles.Count, Is.EqualTo(5_001));
      Assert.That(restored.DetailAt(0).Quantiles.Count, Is.EqualTo(5_000));
      Assert.That(restored.DistinctAt(2).Estimate(), Is.EqualTo(original.DistinctAt(2).Estimate() + 1).Within(50));
      Assert.That(restored.DistinctAt(0).Estimate(), Is.EqualTo(original.DistinctAt(0).Estimate()));
      Assert.That(restored.Distinct, Has.Length.EqualTo(4));
      Assert.That(restored.Details, Has.Length.EqualTo(3));
    });
  }

  [Test]
  public void EntryFramingIsValidated()
  {
    const byte live = 1, details = 4, distinct = 8;
    // One value "x" without moments: name, sum and a count of zero.
    byte[] value = [2, (byte)'x', .. new byte[8], 0];
    Assert.Multiple(() =>
    {
      // A detail entry must hold data: a value without details has length zero.
      Assert.Throws<FormatException>(() => _ = Serializer.Deserialize(
          (byte[])[live | details, 0, 1, .. value, .. Section([1, .. Section(0)])]).DetailAt(0), "empty detail");
      // A distinct entry cannot be empty, and its length must fit the section.
      Assert.Throws<FormatException>(() => _ = Serializer.Deserialize(
          (byte[])[live | distinct, 0, 0, .. Section(1, 2, (byte)'d', 0, 0, 0, 0)]).DistinctNames, "empty distinct sketch");
      Assert.Throws<FormatException>(() => _ = Serializer.Deserialize(
          (byte[])[live | distinct, 0, 0, .. Section(1, 2, (byte)'d', 9, 14, 1, 0)]).DistinctNames, "truncated distinct sketch");
    });
    // A value without details decodes as null.
    Assert.That(Serializer.Deserialize((byte[])[live | details, 0, 1, .. value, .. Section(1, 0)]).DetailAt(0), Is.Null);
  }

  [Test]
  public void AScalarQueryStillSeesVisitorOnlyRows()
  {
    var accumulator = new RollupAccumulator();
    accumulator.AddVisitor(7, HyperLogLog.DefaultPrecision);
    var group = new RollupAccumulator();
    group.MergeForQuery(Serializer.Deserialize(Serializer.Serialize(accumulator.ToValue())), new RollupReadRequirements(false, null, []));
    Assert.That(group.HasData, Is.True);
  }

  [Test]
  public void UndecodedSectionsArePassedThroughByteForByte()
  {
    var bytes = Serializer.Serialize(HeavyRow()).ToArray();
    // Merges and index copies re-serialize stored rows: an untouched section is copied, not re-encoded.
    var reserialized = AllocatedPerCall(() => Serializer.Serialize(Serializer.Deserialize(bytes)));
    Assert.That(Serializer.Serialize(Serializer.Deserialize(bytes)).ToArray(), Is.EqualTo(bytes));
    Assert.That(reserialized, Is.LessThan(bytes.Length + 2_048), $"re-serializing allocated {reserialized:N0} bytes");

    // A delta without sketches keeps the stored sections.
    var delta = new RollupValue(1, null, ["load_time"], [ValueStats.Of(7)]);
    var merged = Serializer.Deserialize(Serializer.Serialize(RollupValue.Merge(Serializer.Deserialize(bytes), delta)));
    var original = Serializer.Deserialize(bytes);
    Assert.Multiple(() =>
    {
      Assert.That(merged.Count, Is.EqualTo(5_001));
      Assert.That(merged.Values[0].Count, Is.EqualTo(5_001));
      Assert.That(merged.Visitors.Estimate(), Is.EqualTo(original.Visitors.Estimate()));
      Assert.That(merged.Distinct[0].Estimate(), Is.EqualTo(original.Distinct[0].Estimate()));
      Assert.That(merged.Details[0].Quantiles.Count, Is.EqualTo(5_000), "the delta added no detail");
    });
  }

  [Test]
  public void RowsReadFromALargeBlockDoNotKeepTheBlockAlive()
  {
    var bytes = Serializer.Serialize(HeavyRow()).ToArray();
    // ZoneTree reads values from decompressed disk blocks of 4 MiB by default.
    var block = new byte[4 * 1024 * 1024];
    bytes.CopyTo(block, 1_000);
    var row = Serializer.Deserialize(block.AsMemory(1_000, bytes.Length));
    // A merged row keeps the sections of the stored row it shares.
    var merged = RollupValue.Merge(row, new RollupValue(1, null, ["load_time"], [ValueStats.Of(1)]));
    static int BackingLength(ReadOnlyMemory<byte> section) =>
        MemoryMarshal.TryGetArray(section, out var segment) ? segment.Array.Length : -1;
    foreach (var value in new[] { row, merged })
    {
      Assert.Multiple(() =>
      {
        Assert.That(BackingLength(value.VisitorSection.Bytes), Is.InRange(1, bytes.Length));
        Assert.That(BackingLength(value.DetailSection.Bytes), Is.InRange(1, bytes.Length));
        Assert.That(BackingLength(value.DistinctSection.Bytes), Is.InRange(1, bytes.Length));
      });
    }
    Assert.Multiple(() =>
    {
      Assert.That(merged.Visitors.Estimate(), Is.EqualTo(5_000).Within(5_000 * 0.03));
      Assert.That(merged.Details[0].Quantiles.Count, Is.EqualTo(5_000));
      Assert.That(Serializer.Serialize(row).ToArray(), Is.EqualTo(bytes));
    });

    // A row read from a buffer of its own exact size is used without a copy.
    var exact = Serializer.Deserialize(bytes);
    Assert.That(MemoryMarshal.TryGetArray(exact.VisitorSection.Bytes, out var own) && ReferenceEquals(own.Array, bytes), Is.True);
  }

  [Test]
  public void ConcurrentReadersShareOneDecodedSketch()
  {
    var row = Serializer.Deserialize(Serializer.Serialize(HeavyRow(1_000)));
    var sketches = new HyperLogLog[64];
    Parallel.For(0, sketches.Length, i => sketches[i] = row.Visitors);
    Assert.That(sketches, Is.All.SameAs(sketches[0]));
    Assert.That(sketches[0].Estimate(), Is.EqualTo(1_000));
  }

  [TestCase(1)]
  [TestCase(100)]
  [TestCase(4_000)]
  [TestCase(4_097)]
  public void SketchesOfEverySizeRoundTrip(int visitors)
  {
    var row = HeavyRow(visitors);
    var restored = Serializer.Deserialize(Serializer.Serialize(row));
    Assert.Multiple(() =>
    {
      Assert.That(restored.Visitors.Estimate(), Is.EqualTo(row.Visitors.Estimate()));
      Assert.That(restored.Visitors.IsSparse, Is.EqualTo(row.Visitors.IsSparse));
      Assert.That(restored.Distinct[0].Estimate(), Is.EqualTo(row.Distinct[0].Estimate()));
      Assert.That(Serializer.Serialize(restored).ToArray(), Is.EqualTo(Serializer.Serialize(row).ToArray()));
    });
  }

  [Test]
  public void RollupReaderRejectsWhatItDoesNotUnderstand()
  {
    var bytes = Serializer.Serialize(HeavyRow(10)).ToArray();
    var unknownBit = bytes.ToArray();
    unknownBit[0] |= 0x10;
    var notPresent = bytes.ToArray();
    notPresent[0] &= 0xFE;
    Assert.Multiple(() =>
    {
      Assert.Throws<FormatException>(() => Serializer.Deserialize(unknownBit), "unknown section");
      Assert.Throws<FormatException>(() => Serializer.Deserialize(notPresent), "sections of a row without the present bit");
      Assert.Throws<FormatException>(() => Serializer.Deserialize(bytes.Append((byte)0).ToArray()), "trailing bytes");
      Assert.Throws<FormatException>(() => Serializer.Deserialize(bytes.AsMemory(0, bytes.Length - 1)), "a truncated section");
    });
  }

  // int.MaxValue as a varint: an array of that many elements exceeds the supported size.
  static readonly byte[] HugeCount = [0xFF, 0xFF, 0xFF, 0xFF, 0x07];

  static byte[] Section(params byte[] payload) => [(byte)payload.Length, .. payload];

  [Test]
  public void CorruptCountsAreFormatErrorsNotAllocations()
  {
    const byte live = 1, visitors = 2, distinct = 8;
    // count 0, then a value count that the remaining bytes cannot hold.
    byte[] values = [live, 0, .. HugeCount];
    // A sparse visitor sketch (precision 14) and a distinct section with an impossible count.
    byte[] sparse = [live | visitors, 0, 0, .. Section([14, 1, .. HugeCount])];
    byte[] dimensions = [live | distinct, 0, 0, .. Section(HugeCount)];
    Assert.Multiple(() =>
    {
      Assert.Throws<FormatException>(() => Serializer.Deserialize(values), "value count");
      Assert.Throws<FormatException>(() => _ = Serializer.Deserialize(sparse).Visitors, "sparse entry count");
      Assert.Throws<FormatException>(() => _ = Serializer.Deserialize(dimensions).Distinct, "distinct count");
    });
  }

  [Test]
  public void ASparseDeltaWithOverflowingBitsIsRejected()
  {
    // One entry whose five-byte delta sets bits above 32 in its last byte.
    byte[] row = [1 | 2, 0, 0, .. Section([14, 1, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F])];
    Assert.Throws<FormatException>(() => _ = Serializer.Deserialize(row).Visitors);
    // The largest valid delta still decodes.
    byte[] largest = [1 | 2, 0, 0, .. Section([14, 1, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F])];
    Assert.That(Serializer.Deserialize(largest).Visitors.Estimate(), Is.EqualTo(1));
  }

  [Test]
  public void RawAndSessionReadersRejectWhatTheyDoNotUnderstand()
  {
    var raw = RawEventCodec.Encode("e", 1000, 1, 0, 0, false, [], []).ToArray();
    var flagsAt = 1 + 1 + 8 + 8 + 1;
    raw[flagsAt] = 0x02;
    Assert.Throws<FormatException>(() => RawEventCodec.TryDecode(raw, 1000, null, default, new RawEvent()), "unknown raw flag");

    var sessions = new SessionStateSerializer(SessionSchema.Standard);
    var session = sessions.Serialize(new SessionState { SessionId = 1 }).ToArray();
    var unknownPresence = session.ToArray();
    unknownPresence[0] = 2;
    Assert.Multiple(() =>
    {
      Assert.Throws<FormatException>(() => sessions.Deserialize(unknownPresence), "unknown session header");
      Assert.Throws<FormatException>(() => sessions.Deserialize(session.Append((byte)0).ToArray()), "trailing session bytes");
    });
  }
}
