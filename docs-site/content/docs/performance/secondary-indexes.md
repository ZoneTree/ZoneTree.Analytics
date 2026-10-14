---
title: Secondary indexes and backfilling
description: Accelerate selected filtered queries with explicit ordered indexes.
---

# Secondary indexes and backfilling

Accelerate selected filtered queries with explicit ordered indexes.

## Dimension indexes

Configured dimensions store complete copies of retained raw events carrying them. Ordered keys narrow eligible equality-filtered scans without following raw-event pointers.

## Rollup indexes

Explicit definitions select an equality dimension and its joint grouping dimensions. The index duplicates rollup keys and values so matching filtered reports can read aggregate rows directly.

## Shared physical stores

Logical dimension indexes share physical partitions. Logical rollup indexes share physical stores within each enabled tier. Partition targets count combined stored copies or rows.

## Startup and coverage

Opening an engine checks configured indexes and backfills when required. Available retained data determines historical coverage; logs report checks, work, and duration.

## Choose combinations deliberately

Indexing trades disk, ingestion work, and startup work for faster eligible queries. The equality-leading dimension matters when choosing combinations.

## Related pages

- [Rollups and query planning](rollups.md)
- [Configuration](../operations/configuration.md)
- [Observability and troubleshooting](../operations/observability.md)

---

[Performance](index.md) · [Documentation home](../index.md)

Previous: [Rollups and query planning](rollups.md)

Next: [Ingestion and storage tuning](tuning.md)
