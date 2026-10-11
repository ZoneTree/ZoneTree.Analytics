using System.Text;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class PublicStorageCodecTests : DirectoryTest
{
  [Test]
  public void PublicCodecsInspectActualEngineRecordsAndRoundTripTheirBytes()
  {
    using var engine = TestEngine.Create(directory, o =>
    {
      o.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
      o.Rollups.Granularities = Granularity.Minute | Granularity.Hour | Granularity.Day;
      o.Rollups.Definitions.Clear();
      o.Rollups.Definitions.Add(new RollupDefinition());
      o.Rollups.Definitions.Add(new RollupDefinition("missing", "empty", "plan"));
    });
    var timestamp = new DateTimeOffset(2026, 3, 8, 12, 34, 56, TimeSpan.FromHours(2));
    engine.Track(new AnalyticsEvent("purchase")
    {
      Site = "shop", Timestamp = timestamp, VisitorId = "visitor",
      Dimensions = { ["plan"] = "café", ["empty"] = "" },
      Values = { ["revenue"] = 49.5 }
    });
    engine.Flush();
    var rawStore = engine.Storage.RawEvents;
    using var rawPartition = rawStore.Acquire(rawStore.GetPartitions().Single());
    using var iterator = rawPartition.ZoneTree.CreateIterator();
    Assert.That(iterator.Next(), Is.True);
    var key = StorageKeyCodec.DecodeRawEvent(iterator.CurrentKey.Span);
    var raw = RawEventCodec.Decode(iterator.CurrentKey.Span, iterator.CurrentValue.Span);
    Assert.Multiple(() =>
    {
      Assert.That(key.Site, Is.EqualTo("shop"));
      Assert.That(key.Timestamp, Is.EqualTo(timestamp.ToUniversalTime()));
      Assert.That(key.Sequence, Is.GreaterThan(0));
      Assert.That(raw.Key, Is.EqualTo(key));
      Assert.That(raw.Timestamp, Is.EqualTo(timestamp.ToUniversalTime()));
      Assert.That(raw.Name, Is.EqualTo("purchase"));
      Assert.That(raw.Visitor, Is.Not.Zero);
      Assert.That(raw.SessionId, Is.Not.Zero);
      Assert.That(raw.SessionStart, Is.EqualTo(timestamp.ToUniversalTime()));
      Assert.That(raw.Interactive, Is.True);
      Assert.That(raw.TryGetDimension("plan", out var plan) && plan == "café", Is.True);
      Assert.That(raw.Values, Does.Contain(new KeyValuePair<string, double>("revenue", 49.5)));
      Assert.That(raw.TryGetValue("revenue", out var revenue) && revenue == 49.5, Is.True);
      Assert.That(RawEventCodec.EncodeKey(raw).ToArray(), Is.EqualTo(iterator.CurrentKey.ToArray()));
      Assert.That(RawEventCodec.Encode(raw).ToArray(), Is.EqualTo(iterator.CurrentValue.ToArray()));
    });
    foreach (var granularity in new[] { Granularity.Minute, Granularity.Hour, Granularity.Day })
    {
      var store = engine.Storage.GetRollups(granularity);
      using var partition = store.Acquire(store.GetPartitions().Single());
      using var rows = partition.ZoneTree.CreateIterator();
      var foundDimensions = false;
      var foundTotals = false;
      while (rows.Next())
      {
        var rollup = StorageKeyCodec.DecodeRollup(rows.CurrentKey.Span);
        Assert.That(StorageKeyCodec.EncodeRollup(rollup).ToArray(), Is.EqualTo(rows.CurrentKey.ToArray()));
        Assert.That(rollup.Site, Is.EqualTo("shop"));
        if (rollup.Event != "purchase")
          continue;
        var bucket = rollup.GetBucketStart(granularity, engine.TimeZone);
        Assert.That(bucket, Is.LessThanOrEqualTo(timestamp));
        if (rollup.Dimensions.Count == 0)
        {
          foundTotals = true;
          continue;
        }
        foundDimensions = true;
        Assert.Multiple(() =>
        {
          Assert.That(rollup.DimensionSetId, Is.EqualTo("empty,missing,plan"));
          Assert.That(rollup.Dimensions["empty"], Is.Empty);
          Assert.That(rollup.Dimensions["missing"], Is.Null);
          Assert.That(rollup.Dimensions["plan"], Is.EqualTo("café"));
        });
      }
      Assert.That(foundDimensions && foundTotals, Is.True);
    }
  }

  [Test]
  public void RawCodecPreservesSessionlessEventsNullValuesAndDeletionMarkers()
  {
    var original = new RawEvent
    {
      Site = "site", Ticks = 1000, Sequence = 2, Name = "job", Visitor = 123,
      Dimensions = { new("nullable", null), new("empty", "") },
      Values = { new("negative", -42.5) }
    };
    var key = RawEventCodec.EncodeKey(original);
    var value = RawEventCodec.Encode(original);
    var decoded = RawEventCodec.Decode(key.Span, value.Span);
    Assert.Multiple(() =>
    {
      Assert.That(decoded.SessionStart, Is.Null);
      Assert.That(decoded.Dimensions, Is.EqualTo(original.Dimensions));
      Assert.That(decoded.Values, Is.EqualTo(original.Values));
      Assert.That(RawEventCodec.Encode(decoded).ToArray(), Is.EqualTo(value.ToArray()));
      Assert.That(RawEventCodec.Decode(key.Span, RawEventCodec.Encode(null).Span), Is.Null);
    });
  }

  [Test]
  public void RollupKeyEncodingSortsDimensionsAndPreservesMissingAndEmptyValues()
  {
    var key = new RollupKey
    {
      Site = "商店", Event = "$session", BucketTicks = 1000,
      Dimensions = { ["z"] = null, ["a"] = "", ["page"] = "/café" }
    };
    var decoded = StorageKeyCodec.DecodeRollup(StorageKeyCodec.EncodeRollup(key).Span);
    Assert.Multiple(() =>
    {
      Assert.That(decoded.Site, Is.EqualTo("商店"));
      Assert.That(decoded.Event, Is.EqualTo("$session"));
      Assert.That(decoded.DimensionSetId, Is.EqualTo("a,page,z"));
      Assert.That(decoded.Dimensions["z"], Is.Null);
      Assert.That(decoded.Dimensions["a"], Is.Empty);
      Assert.That(decoded.Dimensions["page"], Is.EqualTo("/café"));
    });
  }

  [Test]
  public void BucketInterpretationDistinguishesUtcHoursFromLocalDaysAcrossDst()
  {
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    var midnight = new DateTime(2026, 3, 8).Ticks;
    var key = new RollupKey { BucketTicks = midnight };
    Assert.That(key.GetBucketStart(Granularity.Hour, zone), Is.EqualTo(new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero)));
    Assert.That(key.GetBucketStart(Granularity.Day, zone), Is.EqualTo(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero)));
    var next = new RollupKey { BucketTicks = midnight + TimeSpan.TicksPerDay };
    Assert.That(next.GetBucketStart(Granularity.Day, zone) - key.GetBucketStart(Granularity.Day, zone), Is.EqualTo(TimeSpan.FromHours(23)));
    Assert.Throws<ArgumentOutOfRangeException>(() => key.GetBucketStart(Granularity.Hour | Granularity.Day, zone));
  }

  [Test]
  public void RawKeyDecoderRejectsEveryTruncatedKeyAndTrailingBytes()
  {
    var encoded = StorageKeyCodec.EncodeRawEvent(new RawEventKey("s", 100, 1)).ToArray();
    for (var length = 0; length < encoded.Length; ++length)
    {
      var prefix = encoded.AsSpan(0, length).ToArray();
      Assert.Throws<FormatException>(() => StorageKeyCodec.DecodeRawEvent(prefix));
    }
    Assert.Throws<FormatException>(() => StorageKeyCodec.DecodeRawEvent([.. encoded, 0]));
    Assert.Throws<FormatException>(() => StorageKeyCodec.DecodeRawEvent([0xFF, 0, .. encoded.AsSpan(2)]));
  }

  [Test]
  public void RawValueDecoderRejectsTruncationAndTrailingBytes()
  {
    var raw = new RawEvent { Site = "s", Ticks = 100, Sequence = 1, Name = "purchase", Values = { new("value", 1) } };
    var key = RawEventCodec.EncodeKey(raw);
    var value = RawEventCodec.Encode(raw).ToArray();
    for (var length = 1; length < value.Length; ++length)
    {
      var prefix = value.AsSpan(0, length).ToArray();
      Assert.Throws<FormatException>(() => RawEventCodec.Decode(key.Span, prefix));
    }
    Assert.Throws<FormatException>(() => RawEventCodec.Decode(key.Span, [.. value, 0]));
  }

  [Test]
  public void RawValueDecoderRejectsInvalidUtf8OverflowingIntegersAndInvalidSessionStarts()
  {
    var raw = new RawEvent { Site = "s", Ticks = 100, Sequence = 1, Name = "e" };
    var key = RawEventCodec.EncodeKey(raw);
    var invalidUtf8 = RawEventCodec.Encode(raw).ToArray();
    invalidUtf8[1] = 0xFF;
    Assert.Throws<FormatException>(() => RawEventCodec.Decode(key.Span, invalidUtf8));
    Assert.Throws<FormatException>(() => RawEventCodec.Decode(key.Span,
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02]));
    raw.SessionId = 1;
    raw.SessionStartTicks = -1;
    var invalidSession = RawEventCodec.Encode(raw);
    Assert.Throws<FormatException>(() => RawEventCodec.Decode(key.Span, invalidSession.Span));
  }

  [TestCase("")]
  [TestCase("s")]
  [TestCase("s\0e")]
  [TestCase("s\0e\0page")]
  [TestCase("s\0e\0page\0")]
  public void RollupKeyDecoderRejectsTruncatedHeaders(string header) =>
      Assert.Throws<FormatException>(() => StorageKeyCodec.DecodeRollup(Encoding.UTF8.GetBytes(header)));

  [TestCase("page", "a\u001Fb")]
  [TestCase("", "unexpected")]
  [TestCase("a,b", "one")]
  [TestCase("b,a", "one\u001Ftwo")]
  [TestCase("a,a", "one\u001Ftwo")]
  [TestCase("a,", "one\u001Ftwo")]
  public void RollupKeyDecoderRejectsAmbiguousDimensionLayouts(string names, string values)
  {
    var bytes = KeyCodec.RollupKey("s", "e", names, 100, values);
    Assert.Throws<FormatException>(() => StorageKeyCodec.DecodeRollup(bytes.Span));
  }

  [TestCase("a\0b", "value")]
  [TestCase("a,b", "value")]
  [TestCase("page", "a\u001Fb")]
  [TestCase("page", "\u0001")]
  public void KeyEncoderRejectsReservedCharacters(string dimension, string value)
  {
    var key = new RollupKey { Site = "s", Event = "e", BucketTicks = 100, Dimensions = { [dimension] = value } };
    Assert.Throws<ArgumentException>(() => StorageKeyCodec.EncodeRollup(key));
    Assert.Throws<ArgumentException>(() => StorageKeyCodec.EncodeRawEvent(new RawEventKey("s\0e", 100, 1)));
  }
}
