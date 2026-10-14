---
title: Performance
description: Choose aggregation, indexing, and resource settings for your workload.
---

# Performance

Query speed depends on the records a plan must read and the aggregates available to it. Begin with rollups and query plans, then choose explicit index combinations and measure their cost.

## In this section

| Page | Purpose |
| --- | --- |
| [Rollups and query planning](rollups.md) | Understand why some reports read a few aggregate rows while others scan event history. |
| [Secondary indexes and backfilling](secondary-indexes.md) | Accelerate selected filtered queries with explicit ordered indexes. |
| [Ingestion and storage tuning](tuning.md) | Balance ingestion, query latency, memory, and disk for a real workload. |
| [Benchmarking](benchmarking.md) | Compare ingestion and reporting performance with reproducible workloads. |

## Where to go next

Use [Operations](../operations/index.md) to manage configuration changes and startup work. Consult [Configuration reference](../reference/configuration.md) for setting categories.

---

[Documentation home](../index.md)
