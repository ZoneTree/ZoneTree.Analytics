using System.Globalization;
using ZoneTree.Analytics.Ingestion;
using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Sketches;
using ZoneTree.Analytics.Storage;
using ZoneTree.Comparers;

namespace ZoneTree.Analytics.UnitTests;

public sealed class StorageCodecTests
{
  static readonly ByteArrayLexicographicComparerAscending Comparer = new();

  [Test]
  public void RollupKeysOrderBySeriesThenBucketThenValue()
  {
    var keys = new[]
    {
      KeyCodec.RollupKey("site", "pageview", "page", 200, "/a"),
      KeyCodec.RollupKey("site", "pageview", "page", 100, "/b"),
      KeyCodec.RollupKey("site", "pageview", "page", 100, "/a"),
      KeyCodec.RollupKey("site", "pageview", "", 50, ""),
      KeyCodec.RollupKey("site", "pageviewx", "", 10, ""),
      KeyCodec.RollupKey("sit", "pageview", "page", 999, "/z"),
    };
    var sorted = keys.OrderBy(x => x, Comparer.ToComparer()).Select(Describe).ToArray();
    Assert.That(sorted, Is.EqualTo(new[]
    {
      "sit|pageview|page|999|/z",
      "site|pageview||50|",
      "site|pageview|page|100|/a",
      "site|pageview|page|100|/b",
      "site|pageview|page|200|/a",
      "site|pageviewx||10|",
    }));
  }

  static string Describe(Memory<byte> key)
  {
    var span = key.Span;
    var site = KeyCodec.ReadSegment(span, 0, out var next);
    var eventName = KeyCodec.ReadSegment(span, next, out next);
    var set = KeyCodec.ReadSegment(span, next, out next);
    var bucket = KeyCodec.ReadBucket(span, next);
    var value = KeyCodec.ReadValue(span, next);
    return $"{site}|{eventName}|{set}|{bucket}|{value}";
  }

  [Test]
  public void PrefixSortsBeforeItsExtensions()
  {
    // Prefix seeks rely on this: seeking to a prefix must land on the first key that has it.
    var prefix = KeyCodec.SeekKey(KeyCodec.RollupPrefix("s", "e", "page"), 100);
    var row = KeyCodec.RollupKey("s", "e", "page", 100, "/x");
    Assert.Multiple(() =>
    {
      Assert.That(Comparer.Compare(prefix, row), Is.LessThan(0));
      Assert.That(Comparer.Compare(row, prefix), Is.GreaterThan(0));
      Assert.That(Comparer.Compare(row, row.ToArray()), Is.EqualTo(0));
    });
  }

  [Test]
  public void BigEndianBucketsSortNumerically()
  {
    var small = KeyCodec.RollupKey("s", "e", "", 255, "");
    var large = KeyCodec.RollupKey("s", "e", "", 256, "");
    Assert.That(Comparer.Compare(small, large), Is.LessThan(0));
  }

  [Test]
  public void RollupValueSerializationRoundTrips()
  {
    var sketch = new HyperLogLog();
    sketch.Add(1);
    sketch.Add(2);
    var value = new RollupValue(
        42,
        sketch,
        ["$bounces", "revenue"],
        [new MetricValue(-3, 0, 0, 0, 0), new MetricValue(99.5, 3, 10, 50, 600.25)]);
    var serializer = new RollupValueSerializer();
    var restored = serializer.Deserialize(serializer.Serialize(value));
    Assert.Multiple(() =>
    {
      Assert.That(restored.Count, Is.EqualTo(42));
      Assert.That(restored.Visitors.Estimate(), Is.EqualTo(2));
      Assert.That(restored.MetricNames, Is.EqualTo(value.MetricNames));
      Assert.That(restored.Metrics, Is.EqualTo(value.Metrics));
      Assert.That(serializer.Deserialize(serializer.Serialize(null)), Is.Null);
    });
  }

  [Test]
  public void RollupMergeNeverMutatesInputs()
  {
    var a = new RollupAccumulator { Count = 1 };
    a.AddVisitor(1, 14);
    a.AddMetric("x", 5);
    var b = new RollupAccumulator { Count = 2 };
    b.AddVisitor(2, 14);
    b.AddMetric("x", 7);
    b.AddMetric("y", 1);
    var va = a.ToValue();
    var vb = b.ToValue();
    var merged = RollupValue.Merge(va, vb);
    Assert.Multiple(() =>
    {
      Assert.That(merged.Count, Is.EqualTo(3));
      Assert.That(merged.Visitors.Estimate(), Is.EqualTo(2));
      Assert.That(merged.MetricNames, Is.EqualTo(new[] { "x", "y" }));
      Assert.That(merged.Metrics[0], Is.EqualTo(new MetricValue(12, 2, 5, 7, 2)));
      Assert.That(va.Visitors.Estimate(), Is.EqualTo(1));
      Assert.That(vb.Visitors.Estimate(), Is.EqualTo(1));
    });
  }

  [Test]
  public void RawEventRoundTrips()
  {
    var dims = new Dictionary<string, string> { ["page"] = "/a", ["country"] = "DE", ["empty"] = "" };
    var encoded = RawEventCodec.Encode(
        "pageview", 1000, 77, 99, 400, true, dims,
        [new KeyValuePair<string, double>("revenue", 1.5)]);
    var target = new RawEvent();
    Assert.That(RawEventCodec.TryDecode(encoded.Span, 1000, "signup", RawEventCodec.Utf8("signup"), target), Is.False);
    Assert.That(RawEventCodec.TryDecode(encoded.Span, 1000, "pageview", RawEventCodec.Utf8("pageview"), target), Is.True);
    Assert.Multiple(() =>
    {
      Assert.That(target.Name, Is.EqualTo("pageview"));
      Assert.That(target.Visitor, Is.EqualTo(77));
      Assert.That(target.SessionId, Is.EqualTo(99));
      Assert.That(target.SessionStartTicks, Is.EqualTo(400));
      Assert.That(target.Interactive, Is.True);
      Assert.That(target.TryGetDimension("country", out var country) && country == "DE", Is.True);
      Assert.That(target.TryGetDimension("empty", out var empty) && empty == "", Is.True);
      Assert.That(target.Metrics.Single().Value, Is.EqualTo(1.5));
      Assert.That(RawEventCodec.ReadVisitor(encoded.Span), Is.EqualTo(77));
    });
  }

  [Test]
  public void SessionStateRoundTrips()
  {
    var state = new SessionState
    {
      SessionId = 5,
      StartTicks = 100,
      LastTicks = 900,
      ExpiresAtUtcTicks = 12345,
      PageViews = 3,
      Interactions = 4,
      Events = 5,
      EntryPage = "/",
      LastPage = "/b",
      LastHostname = "a.com",
      Dimensions = new string[StandardDimensions.SessionLevel.Count]
    };
    state.Dimensions[0] = "Google";
    var serializer = new SessionStateSerializer(SessionSchema.Standard);
    var restored = serializer.Deserialize(serializer.Serialize(state));
    Assert.Multiple(() =>
    {
      Assert.That(restored.SessionId, Is.EqualTo(5));
      Assert.That(restored.LastTicks, Is.EqualTo(900));
      Assert.That(restored.PageViews, Is.EqualTo(3));
      Assert.That(restored.LastPage, Is.EqualTo("/b"));
      Assert.That(restored.Dimensions[0], Is.EqualTo("Google"));
      Assert.That(restored.Dimensions[1], Is.Null);
    });
  }

  [Test]
  public void SessionStateKeepsCustomDimensionsByName()
  {
    var writerSchema = new SessionSchema(["plan", "ab_variant"]);
    var readerSchema = new SessionSchema(["ab_variant", "team"]);
    var state = new SessionState { SessionId = 1, Dimensions = new string[writerSchema.Count] };
    state.Dimensions[0] = "Google";
    state.Dimensions[writerSchema.IndexOf("plan")] = "pro";
    state.Dimensions[writerSchema.IndexOf("ab_variant")] = "B";
    var restored = new SessionStateSerializer(readerSchema).Deserialize(
        new SessionStateSerializer(writerSchema).Serialize(state));
    Assert.Multiple(() =>
    {
      Assert.That(restored.Dimensions, Has.Length.EqualTo(readerSchema.Count));
      Assert.That(restored.Dimensions[0], Is.EqualTo("Google"));
      Assert.That(restored.Dimensions[readerSchema.IndexOf("ab_variant")], Is.EqualTo("B"));
      Assert.That(restored.Dimensions[readerSchema.IndexOf("team")], Is.Null);
    });
  }

  [Test]
  public void CalendarHandlesTimeZones()
  {
    var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    var calendar = new ReportingCalendar(zone, DayOfWeek.Monday);
    // 2026-03-08 is the DST start day in New York: 23 hours long.
    var day = new DateTime(2026, 3, 8).Ticks;
    var start = calendar.UtcStartOfLocalDay(day);
    var next = calendar.UtcStartOfLocalDay(day + TimeSpan.TicksPerDay);
    Assert.Multiple(() =>
    {
      Assert.That(new DateTime(start), Is.EqualTo(new DateTime(2026, 3, 8, 5, 0, 0)));
      Assert.That(TimeSpan.FromTicks(next - start), Is.EqualTo(TimeSpan.FromHours(23)));
      Assert.That(calendar.LocalDay(start), Is.EqualTo(day));
      Assert.That(calendar.LocalDay(start - 1), Is.EqualTo(day - TimeSpan.TicksPerDay));
      Assert.That(calendar.CeilToDayBoundary(start + 1), Is.EqualTo(next));
      Assert.That(calendar.FloorToDayBoundary(next - 1), Is.EqualTo(start));
      Assert.That(new DateTime(calendar.LocalBucket(day, TimeInterval.Week)), Is.EqualTo(new DateTime(2026, 3, 2)));
      Assert.That(new DateTime(calendar.LocalBucket(day, TimeInterval.Month)), Is.EqualTo(new DateTime(2026, 3, 1)));
    });
  }

  [Test]
  public void PartitionSchemeNamesAndRanges()
  {
    var scheme = new PartitionScheme(PartitionPeriod.Month);
    var names = scheme.NamesInRange(new DateTime(2026, 1, 15).Ticks, new DateTime(2026, 3, 1).Ticks).ToArray();
    Assert.That(names, Is.EqualTo(new[] { "2026-01", "2026-02" }));
    Assert.That(scheme.TryParse("2026-02", out var start) && new DateTime(start) == new DateTime(2026, 2, 1), Is.True);
    Assert.That(scheme.TryParse("garbage", out _), Is.False);
  }

  [TestCase(PartitionPeriod.Day, "2025-12-31", "2026-01-01", "2026-01-02")]
  [TestCase(PartitionPeriod.Month, "2025-12", "2026-01", "2026-02")]
  [TestCase(PartitionPeriod.Year, "2025", "2026", null)]
  public void PartitionSchemeOfEveryPeriod(PartitionPeriod period, string first, string second, string third)
  {
    var scheme = new PartitionScheme(period);
    var names = scheme.NamesInRange(new DateTime(2025, 12, 31, 23, 0, 0).Ticks, new DateTime(2026, 2, 1, 0, 0, 1).Ticks).ToArray();
    var expected = period == PartitionPeriod.Day
        ? Enumerable.Range(0, 33).Select(x => new DateTime(2025, 12, 31).AddDays(x).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray()
        : new[] { first, second, third }.Where(x => x != null).ToArray();
    Assert.Multiple(() =>
    {
      Assert.That(names, Is.EqualTo(expected));
      Assert.That(names.Take(3).Where(x => x != null), Is.EqualTo(new[] { first, second, third }.Where(x => x != null)));
      Assert.That(scheme.TryParse(second, out var start) && scheme.NameOf(start) == second, Is.True);
      Assert.That(scheme.NextStart(scheme.StartOf(start)), Is.GreaterThan(start));
    });
  }
}
