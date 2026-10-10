It's capable for an embedded engine, with a few real gaps. Here is what the code shows.

### What it handles well
- **Planning:**
  - Each query is split across daily, hourly and minute rollups, plus raw events for the unaligned edges of the range.
  - The plan is reported in `QueryPlan`, with warnings when a result may be incomplete.
- **Execution:**
  - Raw scans run in parallel time slices.
  - One query can combine metrics from several events, joined on bucket and group.
- **Analysis:**
  - Conditional metrics (`Where`), formulas, series functions such as rate, moving average, lag and lead.
  - Percentiles, histograms, distinct counts, comparison periods, `Having`, `LimitPerBucket` and gap filling.
  - Segments by visitor or session, and funnels with up to 64 steps.


### Limitations that matter in practice
1. **Fast answers need an exact rollup match.** A query is answered from rollups only when its group-by plus filter dimensions exactly match a `RollupDefinition`. Any other combination scans raw events:
   - That scales with the number of events: ~16–60 ms per 30 days at about 860k events/month, so seconds at hundreds of millions.
   - It is only possible within raw event retention (90 days by default).
   - Beyond that, the query fails or returns a partial result with a warning.
2. **No cap on group count in raw scans.** Grouping raw events by a high-cardinality dimension (full URLs with query strings, user ids) builds every group in memory, each with a visitor sketch, before sorting and applying `Limit`. That could use a lot of memory; nothing currently stops it except the timeout.
3. **Filters combine with AND only.** One filter can match several values (`country==DE|AT`), but there is no OR across dimensions, such as "country is DE or source is Google".
4. **No grouping by derived time.** There's no hour-of-day or day-of-week grouping, so a "traffic by weekday" heatmap isn't possible.
5. **Session metrics are restricted.** Bounce rate, duration and sessions can only be grouped or filtered by session-level dimensions (source, country, device, entry page and similar). "Bounce rate by page" is rejected with an error.
6. **Segments and funnels need raw events.** They only work within raw event retention.
7. **Counts are approximate in places.** Visitor and distinct counts are HyperLogLog sketches: exact up to 4,096 per row, about 0.8% error beyond. Percentiles are within 1%.
8. **No cohort or retention analysis.** That needs visitor identity across days, which the default daily-rotating hash rules out by design.
9. **No query result cache.** Repeated dashboard queries rely on the storage block cache.

### What I'd add, in order
1. **A cap on raw-scan groups** (for example 100k, configurable), failing with a clear message. This is a protection gap, not a missing feature.
2. **OR filter groups** (`AnyOf(filter, filter)`): small, and commonly needed.
3. **Derived time dimensions** (`$hour_of_day`, `$day_of_week`): these can be computed from the bucket on both rollups and raw events, so no new storage is needed.
4. **Rollup suggestions:** when a query falls back to raw events, the plan could name the `RollupDefinition` that would have answered it from rollups.

Should I implement any of these?