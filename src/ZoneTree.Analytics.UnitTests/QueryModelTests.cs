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
}
