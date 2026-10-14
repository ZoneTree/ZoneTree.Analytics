---
title: Observability and troubleshooting
description: Explain startup cost, ingestion health, and report latency using engine evidence.
---

# Observability and troubleshooting

Explain startup cost, ingestion health, and report latency using engine evidence.

## Inspect ingestion health

Use engine statistics and logs to distinguish accepted, ingested, filtered, rejected, dropped, and failed work. Queue and buffer pressure help explain delayed visibility.

## Inspect startup work

Backfill necessity checks, start and completion logs, record counts, and durations expose the cost of dimension and rollup index initialization.

## Inspect query plans

Find whether slow reports use rollups, indexed reads, raw scans, or mixed coverage. Compare filter dimensions and grouping combinations before changing storage settings.

## Diagnose incomplete or delayed reports

Check flush visibility, retention, activation dates, site selection, and query warnings. For host errors, also check authorization and reporting limits.

## Use advanced storage access carefully

The engine exposes owned stores for inspection. Logical index access includes a key prefix; direct writes or independent maintenance can bypass analytics invariants.

## Related pages

- [Engine API reference](../reference/engine.md)
- [Rollups and query planning](../performance/rollups.md)
- [Secondary indexes and backfilling](../performance/secondary-indexes.md)

---

[Operations](index.md) · [Documentation home](../index.md)

Previous: [Deployment and lifecycle](deployment.md)
