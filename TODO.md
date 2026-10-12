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
2. **Session metrics are restricted.** Bounce rate, duration and sessions can only be grouped or filtered by session-level dimensions (source, country, device, entry page and similar), plus derived time dimensions. "Bounce rate by page" is rejected with an error.
3. **Segments and funnels need raw events.** They only work within raw event retention.
4. **Counts are approximate in places.** Visitor and distinct counts are HyperLogLog sketches: exact up to 4,096 per row, about 0.8% error beyond. Percentiles are within 1%.
5. **No cohort or retention analysis.** That needs visitor identity across days, which the default daily-rotating hash rules out by design.
6. **No query result cache.** Repeated dashboard queries rely on the storage block cache.

### Completed improvements
1. **Group cap:** `Queries.MaxGroups` defaults to 100,000 and rejects queries before allocating excess aggregate groups, across raw scans and rollups.
2. **OR filter groups:** `Filter.AnyOf` and `Filter.AllOf` support nested conditions across queries, conditional metrics, segments, funnels and the reporting API.
3. **Derived time dimensions:** `$hour_of_day` and `$day_of_week` support grouping and filtering in the query's time zone without new storage. The planner selects compatible rollup granularities or raw events.
4. **Rollup suggestions:** `QueryPlan.RollupSuggestions` identifies exact missing definitions for automatic raw fallbacks, also exposed by the reporting API. Suggestions apply to newly tracked data; existing events are not backfilled.
