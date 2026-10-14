---
title: Time, freshness, and accuracy
description: Understand what a report's time range, visibility, and counts mean.
---

# Time, freshness, and accuracy

Understand what a report's time range, visibility, and counts mean.

## Reporting time

Reporting time zones and calendar boundaries affect buckets, comparisons, and date ranges. The engine persists its reporting time zone when the directory is created.

## Freshness

Accepted events pass through background ingestion and rollup flushing. A dashboard refresh can therefore show several visits arriving together rather than one increment per request.

## Exact and approximate metrics

Visitor counts and percentiles use mergeable sketches where appropriate. Interpret each metric according to its aggregation and accuracy guarantees.

## Coverage and completeness

Retention, configured aggregates, and activation dates affect which historical questions can be answered. Inspect query plans and warnings when a report is incomplete.

## Related pages

- [Privacy and identity](privacy.md)
- [Rollups and query planning](../performance/rollups.md)
- [Retention and visitor erasure](../operations/retention-and-erasure.md)

---

[Concepts](index.md) · [Documentation home](../index.md)

Previous: [Event and report model](data-model.md)

Next: [Privacy and identity](privacy.md)
