---
title: Rollups and query planning
description: Understand why some reports read a few aggregate rows while others scan event history.
---

# Rollups and query planning

Understand why some reports read a few aggregate rows while others scan event history.

## Pre-aggregate common questions

Rollup definitions describe dimension combinations aggregated during ingestion. Enabled time tiers provide different historical resolutions.

## Match a query to available data

The planner considers dimensions, metrics, time boundaries, retention, and activation coverage. A request can use rollups for most of its range and raw events for uncovered parts.

## Add optional aggregate state

Percentiles, first and last values, and distinct dimension counts can require additional state in each rollup row. Enable it for measurements that need those reports.

## Read plans before tuning

Use plan sources, warnings, and rollup suggestions to identify missing coverage. Ordinary new rollup definitions and index-managed backfills have different historical behavior.

## Related pages

- [Secondary indexes and backfilling](secondary-indexes.md)
- [Querying and time series](../guides/querying.md)
- [Observability and troubleshooting](../operations/observability.md)

---

[Performance](index.md) · [Documentation home](../index.md)

Next: [Secondary indexes and backfilling](secondary-indexes.md)
