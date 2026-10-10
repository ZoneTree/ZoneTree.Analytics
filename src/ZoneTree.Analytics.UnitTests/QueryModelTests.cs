using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.UnitTests;

public sealed class QueryModelTests
{
  [TestCase("country==DE", "country", FilterOperator.Equal, new[] { "DE" }, false)]
  [TestCase("country==DE|US", "country", FilterOperator.Equal, new[] { "DE", "US" }, false)]
  [TestCase("page!=/", "page", FilterOperator.NotEqual, new[] { "/" }, false)]
  [TestCase("page~=docs", "page", FilterOperator.Contains, new[] { "docs" }, false)]
  [TestCase("page!~=docs", "page", FilterOperator.NotContains, new[] { "docs" }, false)]
  [TestCase("page^=/blog", "page", FilterOperator.StartsWith, new[] { "/blog" }, false)]
  [TestCase("page$=.html", "page", FilterOperator.EndsWith, new[] { ".html" }, false)]
  [TestCase(@"page=~^/blog/\d+", "page", FilterOperator.Matches, new[] { @"^/blog/\d+" }, false)]
  [TestCase("page!=~^/admin", "page", FilterOperator.NotMatches, new[] { "^/admin" }, false)]
  [TestCase("browser==/iCHROME", "browser", FilterOperator.Equal, new[] { "CHROME" }, true)]
  [TestCase("page==/search?", "page", FilterOperator.Equal, new[] { "/search?" }, false)]
  [TestCase(@"tag==x\|y|z", "tag", FilterOperator.Equal, new[] { "x|y", "z" }, false)]
  [TestCase("plan?", "plan", FilterOperator.IsSet, new string[0], false)]
  [TestCase("!plan?", "plan", FilterOperator.IsNotSet, new string[0], false)]
  public void ParsesFilters(string expression, string dimension, FilterOperator op, string[] values, bool ignoreCase)
  {
    var filter = Filter.Parse(expression);
    Assert.Multiple(() =>
    {
      Assert.That(filter.Dimension, Is.EqualTo(dimension));
      Assert.That(filter.Operator, Is.EqualTo(op));
      Assert.That(filter.Values, Is.EqualTo(values));
      Assert.That(filter.IgnoreCase, Is.EqualTo(ignoreCase));
      Assert.That(Filter.Parse(filter.ToString()).ToString(), Is.EqualTo(filter.ToString()));
    });
  }

  [Test]
  public void RejectsInvalidFilters()
  {
    Assert.Throws<FormatException>(() => Filter.Parse("country"));
    Assert.Throws<ArgumentException>(() => Filter.Equal("country"));
    Assert.Throws<ArgumentException>(() => Filter.Equal("country", (string)null));
  }

  [Test]
  public void FilterSemanticsForMissingValues()
  {
    Assert.Multiple(() =>
    {
      Assert.That(Filter.Equal("d", "x").IsMatch(null), Is.False);
      Assert.That(Filter.NotEqual("d", "x").IsMatch(null), Is.True);
      Assert.That(Filter.Contains("d", "x").IsMatch(null), Is.False);
      Assert.That(Filter.NotContains("d", "x").IsMatch(null), Is.True);
      Assert.That(Filter.IsSet("d").IsMatch(""), Is.True);
      Assert.That(Filter.IsNotSet("d").IsMatch(null), Is.True);
      Assert.That(Filter.Equal("d", "").IsMatch(""), Is.True);
      Assert.That(new Filter("d", FilterOperator.Equal, ignoreCase: true, "ABC").IsMatch("abc"), Is.True);
    });
  }

  [TestCase("events")]
  [TestCase("visitors")]
  [TestCase("pageviews")]
  [TestCase("sessions")]
  [TestCase("bounces")]
  [TestCase("bounce_rate")]
  [TestCase("session_duration")]
  [TestCase("avg_session_duration")]
  [TestCase("pageviews_per_session")]
  [TestCase("entrances")]
  [TestCase("exits")]
  [TestCase("exit_rate")]
  [TestCase("events:signup")]
  [TestCase("visitors:signup")]
  [TestCase("sum:revenue")]
  [TestCase("avg:duration_ms@job")]
  [TestCase("min:revenue")]
  [TestCase("max:revenue")]
  [TestCase("count:revenue")]
  [TestCase("conversion_rate:signup")]
  [TestCase("stddev:load_time")]
  [TestCase("variance:load_time@job")]
  [TestCase("p95:load_time")]
  [TestCase("p99.9:load_time@job")]
  [TestCase("p50:load_time")]
  [TestCase("first:temperature")]
  [TestCase("last:temperature")]
  [TestCase("distinct:country")]
  [TestCase("distinct:tenant@signup")]
  [TestCase("percent(visitors)")]
  [TestCase("percent(sum:revenue)")]
  [TestCase("add(events,visitors)")]
  [TestCase("sub(events,1)")]
  [TestCase("mul(sum:bytes,8)")]
  [TestCase("div(sum:revenue,events:purchase)")]
  [TestCase("div(add(sum:a,sum:b),0.5)")]
  [TestCase("rate(events)")]
  [TestCase("rate(events,60)")]
  [TestCase("cumulative(sum:revenue)")]
  [TestCase("moving_avg(visitors,7)")]
  [TestCase("diff(last:temperature)")]
  [TestCase("increase(last:energy)")]
  [TestCase("percent(cumulative(div(sum:revenue,events)))")]
  public void MetricKeysRoundTrip(string key)
  {
    var metric = Metric.Parse(key);
    Assert.Multiple(() =>
    {
      Assert.That(metric.Key, Is.EqualTo(key));
      Assert.That(metric, Is.EqualTo(Metric.Parse(key)));
    });
  }

  [Test]
  public void RejectsInvalidMetrics()
  {
    Assert.Throws<FormatException>(() => Metric.Parse("nope"));
    Assert.Throws<FormatException>(() => Metric.Parse("median:x"));
    Assert.Throws<ArgumentException>(() => Metric.Sum("a:b"));
    Assert.Throws<ArgumentException>(() => Metric.Sum("a(b)"));
    Assert.Throws<ArgumentOutOfRangeException>(() => Metric.Percentile("x", 101));
    Assert.Throws<ArgumentOutOfRangeException>(() => Metric.MovingAverage(Metric.Visitors, 1));
    Assert.Throws<ArgumentOutOfRangeException>(() => Metric.Rate(Metric.Visitors, TimeSpan.Zero));
    Assert.Throws<ArgumentOutOfRangeException>(() => Metric.Constant(double.NaN));
    Assert.Throws<FormatException>(() => Metric.Parse("unknown(visitors)"));
    Assert.Throws<FormatException>(() => Metric.Parse("div(visitors)"));
    Assert.Throws<FormatException>(() => Metric.Parse("add(visitors,events"));
    Assert.Throws<FormatException>(() => Metric.Parse("moving_avg(visitors,1)"));
    Assert.Throws<FormatException>(() => Metric.Parse("p101:x"));
  }

  [Test]
  public void DateRangesValidateArguments()
  {
    Assert.Throws<ArgumentException>(() => DateRange.Between(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1)));
    Assert.Throws<ArgumentOutOfRangeException>(() => DateRange.LastDays(0));
    Assert.Throws<ArgumentException>(() => DateRange.Days(new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 1)));
    var february = DateRange.Month(2026, 2);
    Assert.That(february, Is.EqualTo(DateRange.Month(2026, 2)));
    Assert.That(february, Is.Not.EqualTo(DateRange.Month(2026, 3)));
  }

  [Test]
  public void FilterFactoriesMatchLikeTheirOperators()
  {
    var startsWith = Filter.StartsWith("page", "/docs", "/blog");
    var endsWith = Filter.EndsWith("page", ".pdf");
    var notMatches = Filter.NotMatches("page", "^/docs");
    var greaterThan = Filter.GreaterThan("load_time", 100);
    var lessThanOrEqual = Filter.LessThanOrEqual("load_time", 100);
    Assert.Multiple(() =>
    {
      Assert.That(startsWith.Operator, Is.EqualTo(FilterOperator.StartsWith));
      Assert.That(startsWith.IsMatch("/docs/api"), Is.True);
      Assert.That(startsWith.IsMatch("/blog"), Is.True);
      Assert.That(startsWith.IsMatch("/pricing"), Is.False);
      Assert.That(startsWith.IsMatch(null), Is.False, "a missing dimension never matches a positive operator");

      Assert.That(endsWith.Operator, Is.EqualTo(FilterOperator.EndsWith));
      Assert.That(endsWith.IsMatch("/guide.pdf"), Is.True);
      Assert.That(endsWith.IsMatch("/guide.PDF"), Is.False, "comparisons are case-sensitive by default");
      Assert.That(endsWith.IsMatch(null), Is.False);

      Assert.That(notMatches.Operator, Is.EqualTo(FilterOperator.NotMatches));
      Assert.That(notMatches.IsMatch("/pricing"), Is.True);
      Assert.That(notMatches.IsMatch("/docs/api"), Is.False);
      Assert.That(notMatches.IsMatch(null), Is.True, "a missing dimension matches a negative operator");

      Assert.That(greaterThan.Operator, Is.EqualTo(FilterOperator.GreaterThan));
      Assert.That(greaterThan.IsMatch("150"), Is.True);
      Assert.That(greaterThan.IsMatch("100"), Is.False);
      Assert.That(greaterThan.IsMatch("fast"), Is.False, "values that are not numbers never match");
      Assert.That(greaterThan.IsMatch(null), Is.False);

      Assert.That(lessThanOrEqual.Operator, Is.EqualTo(FilterOperator.LessThanOrEqual));
      Assert.That(lessThanOrEqual.IsMatch("100"), Is.True);
      Assert.That(lessThanOrEqual.IsMatch("100.5"), Is.False);

      // Every filter survives a round trip through its expression.
      foreach (var filter in new[] { startsWith, endsWith, notMatches, greaterThan, lessThanOrEqual })
      {
        var parsed = Filter.Parse(filter.ToString());
        Assert.That(parsed.Operator, Is.EqualTo(filter.Operator), filter.ToString());
        Assert.That(parsed.Dimension, Is.EqualTo(filter.Dimension), filter.ToString());
        Assert.That(parsed.ToString(), Is.EqualTo(filter.ToString()));
      }
      Assert.That(startsWith.ToString(), Is.EqualTo("page^=/docs|/blog"));
      Assert.That(greaterThan.ToString(), Is.EqualTo("load_time>100"));
    });
  }

  [TestCase(nameof(MetricFilter.Equal), ComparisonOperator.Equal, "visitors==10", 10, 11)]
  [TestCase(nameof(MetricFilter.NotEqual), ComparisonOperator.NotEqual, "visitors!=10", 11, 10)]
  [TestCase(nameof(MetricFilter.GreaterThan), ComparisonOperator.GreaterThan, "visitors>10", 11, 10)]
  [TestCase(nameof(MetricFilter.GreaterThanOrEqual), ComparisonOperator.GreaterThanOrEqual, "visitors>=10", 10, 9)]
  [TestCase(nameof(MetricFilter.LessThan), ComparisonOperator.LessThan, "visitors<10", 9, 10)]
  [TestCase(nameof(MetricFilter.LessThanOrEqual), ComparisonOperator.LessThanOrEqual, "visitors<=10", 10, 11)]
  public void MetricFilterFactoriesCompareLikeTheirOperators(
      string factory, ComparisonOperator expected, string expression, double matching, double notMatching)
  {
    var filter = factory switch
    {
      nameof(MetricFilter.Equal) => MetricFilter.Equal(Metric.Visitors, 10),
      nameof(MetricFilter.NotEqual) => MetricFilter.NotEqual(Metric.Visitors, 10),
      nameof(MetricFilter.GreaterThan) => MetricFilter.GreaterThan(Metric.Visitors, 10),
      nameof(MetricFilter.GreaterThanOrEqual) => MetricFilter.GreaterThanOrEqual(Metric.Visitors, 10),
      nameof(MetricFilter.LessThan) => MetricFilter.LessThan(Metric.Visitors, 10),
      _ => MetricFilter.LessThanOrEqual(Metric.Visitors, 10),
    };
    var parsed = MetricFilter.Parse(expression);
    Assert.Multiple(() =>
    {
      Assert.That(filter.Operator, Is.EqualTo(expected));
      Assert.That(filter.Metric, Is.EqualTo(Metric.Visitors));
      Assert.That(filter.Value, Is.EqualTo(10));
      Assert.That(filter.IsMatch(matching), Is.True);
      Assert.That(filter.IsMatch(notMatching), Is.False);
      Assert.That(filter.ToString(), Is.EqualTo(expression));
      Assert.That((parsed.Metric, parsed.Operator, parsed.Value), Is.EqualTo((filter.Metric, filter.Operator, filter.Value)));
    });
  }

  [Test]
  public void DescriptionsNameWhatTheyDescribe()
  {
    Assert.Multiple(() =>
    {
      Assert.That(Metric.Sum("revenue").ToString(), Is.EqualTo("sum:revenue"));
      Assert.That(Metric.Percentile("load_time", 95).ToString(), Is.EqualTo(Metric.Percentile("load_time", 95).Key));
      Assert.That(Order.By(Metric.Visitors).ToString(), Is.EqualTo("-visitors"));
      Assert.That(Order.ByDimension(StandardDimensions.Page).ToString(), Is.EqualTo("page"));
      Assert.That(Order.ByBucket(descending: true).ToString(), Is.EqualTo("-bucket"));
      Assert.That(new RollupDefinition().ToString(), Is.EqualTo("(totals)"));
      Assert.That(new RollupDefinition("page", "country").ToString(), Is.EqualTo("country,page"));
      var buyers = Segment.VisitorsWho("purchase", Filter.Equal("plan", "pro"));
      Assert.That(buyers.ToString(), Is.EqualTo("visitors with purchase where plan==pro"));
      Assert.That(buyers.Exclude().ToString(), Is.EqualTo("not visitors with purchase where plan==pro"));
      Assert.That(Segment.SessionsWith("signup").ToString(), Is.EqualTo("sessions with signup"));
      Assert.That(DateRange.Day(new DateOnly(2026, 2, 28)).ToString(), Is.EqualTo("2026-02-28..2026-02-28"));
      Assert.That(DateRange.Month(2026, 3).ToString(), Is.EqualTo("2026-03"));
      Assert.That(DateRange.Year(2025).ToString(), Is.EqualTo("2025"));
      Assert.That(DateRange.LastMonth.ToString(), Is.EqualTo("month -1 relative to this month"));
    });
  }

  [Test]
  public void DateRangesResolveToCalendarBoundaries()
  {
    var utc = new ReportingCalendar(TimeZoneInfo.Utc, DayOfWeek.Monday);
    var plusNine = new ReportingCalendar(
        TimeZoneInfo.CreateCustomTimeZone("plus-nine", TimeSpan.FromHours(9), "plus-nine", "plus-nine"), DayOfWeek.Monday);
    static long Ticks(int year, int month, int day, int hour = 0) => new DateTime(year, month, day, hour, 0, 0).Ticks;
    var march15 = Ticks(2026, 3, 15, 12);
    var january15 = Ticks(2026, 1, 15, 12);
    Assert.Multiple(() =>
    {
      Assert.That(DateRange.Day(new DateOnly(2026, 2, 28)).Resolve(utc, march15), Is.EqualTo((Ticks(2026, 2, 28), Ticks(2026, 3, 1))));
      // Local midnight of a zone 9 hours ahead of UTC is 15:00 UTC of the day before.
      Assert.That(DateRange.Day(new DateOnly(2026, 2, 28)).Resolve(plusNine, march15), Is.EqualTo((Ticks(2026, 2, 27, 15), Ticks(2026, 2, 28, 15))));
      Assert.That(DateRange.Year(2025).Resolve(utc, march15), Is.EqualTo((Ticks(2025, 1, 1), Ticks(2026, 1, 1))));
      Assert.That(DateRange.LastMonth.Resolve(utc, march15), Is.EqualTo((Ticks(2026, 2, 1), Ticks(2026, 3, 1))));
      Assert.That(DateRange.LastMonth.Resolve(utc, january15), Is.EqualTo((Ticks(2025, 12, 1), Ticks(2026, 1, 1))), "last month of January");
    });
  }

  [Test]
  public void EqualRangesAndIntervalsAreInterchangeable()
  {
    var day = new DateOnly(2026, 2, 28);
    var single = DateRange.Day(day);
    var span = DateRange.Days(day, day);
    var other = DateRange.Day(day.AddDays(1));
    Assert.Multiple(() =>
    {
      Assert.That(single == span, Is.True);
      Assert.That(single != other, Is.True);
      Assert.That(single.GetHashCode(), Is.EqualTo(span.GetHashCode()));
      Assert.That(new HashSet<DateRange> { single, span, other }, Has.Count.EqualTo(2));
      // A minute width that is a whole number of hours is that hourly interval.
      Assert.That(TimeInterval.Minutes(120), Is.EqualTo(TimeInterval.Hours(2)));
      Assert.That(TimeInterval.Minutes(120).GetHashCode(), Is.EqualTo(TimeInterval.Hours(2).GetHashCode()));
      Assert.That(new HashSet<TimeInterval> { TimeInterval.Minutes(60), TimeInterval.Hour, TimeInterval.Day }, Has.Count.EqualTo(2));
    });
  }
}
