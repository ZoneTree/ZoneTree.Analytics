---
title: Engine API reference
description: Map the public entry points for tracking, reporting, maintenance, and inspection.
---

# Engine API reference

Map the public entry points for tracking, reporting, maintenance, and inspection.

## Engine and tracking

Cover `AnalyticsEngine`, `IAnalyticsTracker`, `AnalyticsEvent`, and `IngestionResult`: creation, lifetime, event submission, and submission outcomes.

## Reporting entry points

Cover general queries, funnel queries, web reports, and event discovery. Document synchronous and asynchronous forms together.

## Maintenance and privacy

Cover flush, compaction, retention application, visitor erasure, and disposal, including cancellation and completion behavior.

## Statistics and storage access

Cover `AnalyticsStatistics` and `AnalyticsStorage`, shared physical stores, logical index prefixes, and engine ownership rules.

## Errors and cancellation

Cover analytics exception types, argument validation, cancellation tokens, and query timeouts.

## Related pages

- [First events and reports](../getting-started/first-events.md)
- [Query model reference](query-model.md)
- [Observability and troubleshooting](../operations/observability.md)

---

[Reference](index.md) · [Documentation home](../index.md)

Next: [Query model reference](query-model.md)
