using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class RawContinuationRegressionTests : DirectoryTest
{
  [TestCase(0, false)]
  [TestCase(126, false)]
  [TestCase(127, false)]
  [TestCase(300, true)]
  public void SessionIdReaderMatchesFullDecodingAndNeedsOnlyTheHeader(int nameLength, bool unicode)
  {
    var name = new string(unicode ? '界' : 'a', nameLength);
    var prefix = new ByteWriter(32);
    prefix.WriteString(name);
    var headerLength = prefix.ToArray().Length + 16;
    foreach (var sessionId in new[] { 0UL, 0xFEDCBA9876543210UL })
    {
      var encoded = RawEventCodec.Encode(name, 1000, 77, sessionId, 400, true,
          new Dictionary<string, string> { ["page"] = "/later" }, [new("amount", 12)]);
      var decoded = new RawEvent();
      Assert.That(RawEventCodec.TryDecode(encoded.Span, 1000, null, default, decoded), Is.True);
      Assert.That(RawEventCodec.ReadSessionId(encoded.Span), Is.EqualTo(decoded.SessionId));
      Assert.That(RawEventCodec.ReadSessionId(encoded.Span[..headerLength]), Is.EqualTo(sessionId));
    }
  }

  [TestCase(1)]
  [TestCase(4)]
  public void ContinuationKeepsTrackedActivityAndRejectsNewSessionsOfTheSameVisitor(int parallelism)
  {
    var day = DateTime.UtcNow.Date.AddDays(-2);
    using var engine = TestEngine.Create(directory, o => o.Queries.MaxDegreeOfParallelism = parallelism);
    void View(string visitor, int minute) => engine.Track(new AnalyticsEvent("pageview")
    {
      VisitorId = visitor, Url = "/" + visitor, Timestamp = Utc(day.AddMinutes(minute)),
      Values = { ["amount"] = minute }
    });
    View("alice", 0);
    View("bob", 0);
    // Activity exactly at the inactivity deadline extends Alice's original session.
    engine.Track(new AnalyticsEvent("活動") { VisitorId = "alice", Timestamp = Utc(day.AddMinutes(30)) });
    View("alice", 60);
    // These visitors were absent from the requested range, or have started new sessions.
    View("bob", 31);
    View("alice", 91);
    for (var minute = 11; minute < 120; ++minute)
      View("unrelated-" + minute, minute);
    engine.Flush();
    var result = engine.Query(new AnalyticsQuery
    {
      Range = Range(day, day.AddMinutes(10)), Source = QuerySource.RawEvents,
      Metrics =
      {
        Metric.PageViews, Metric.Sessions, Metric.SessionDuration, Metric.PageViewsPerSession,
        Metric.BounceRate, Metric.Entrances, Metric.Exits
      }
    });
    Assert.Multiple(() =>
    {
      Assert.That(result.Totals[Metric.PageViews], Is.EqualTo(2));
      Assert.That(result.Totals[Metric.Sessions], Is.EqualTo(2));
      Assert.That(result.Totals[Metric.SessionDuration], Is.EqualTo(TimeSpan.FromHours(1).TotalSeconds));
      Assert.That(result.Totals[Metric.PageViewsPerSession], Is.EqualTo(1.5));
      Assert.That(result.Totals[Metric.BounceRate], Is.EqualTo(0.5));
      Assert.That(result.Totals[Metric.Entrances], Is.EqualTo(2));
      Assert.That(result.Totals[Metric.Exits], Is.EqualTo(1));
    });
  }
}
