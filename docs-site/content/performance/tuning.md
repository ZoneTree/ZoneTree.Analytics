---
title: Ingestion and storage tuning
description: Balance ingestion, query latency, memory, and disk for a real workload.
---

# Ingestion and storage tuning

Balance ingestion, query latency, memory, and disk for a real workload.

## Measure the workload

Record event size, dimension cardinality, query patterns, retention, and concurrent reporting load before choosing settings.

## Tune ingestion

Worker counts, queue behavior, flush intervals, and aggregation buffers affect throughput and freshness together.

## Tune physical storage

Partition targets, mutable segments, caches, compression, and write-ahead logs affect open-tree memory and disk work.

## Tune query concurrency

Raw scans can use multiple workers. Balance per-query parallelism with the number of concurrent reports the host must serve.

## Account for indexes

Include duplicated event payloads and rollup state in capacity planning. Avoid treating each logical index as an independent physical tree.

## Related pages

- [Benchmarking](benchmarking.md)
- [Secondary indexes and backfilling](secondary-indexes.md)
- [Configuration reference](../reference/configuration.md)

---

[Performance](index.md) · [Documentation home](../index.md)

Previous: [Secondary indexes and backfilling](secondary-indexes.md)

Next: [Benchmarking](benchmarking.md)
