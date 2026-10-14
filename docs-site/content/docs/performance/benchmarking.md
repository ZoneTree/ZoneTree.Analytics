---
title: Benchmarking
description: Compare ingestion and reporting performance with reproducible workloads.
---

# Benchmarking

Compare ingestion and reporting performance with reproducible workloads.

## Choose a representative dataset

Event fields, visitor reuse, session behavior, distributions, optional aggregates, and indexes affect the work per event. Preserve them when comparing runs.

## Use the available tools

The playground exercises the library directly. Dashboard tools provide realistic sample datasets and ingestion benchmarks with progress and retained reports.

## Separate benchmark phases

Record preparation, submission, and ingestion including the final flush. Submission measures handing events to the ingestion pipeline; queued processing can continue afterward.

## Measure query paths

Compare filtered and unfiltered reports, ranges, cardinality, query plans, and memory versus disk state. Similar event counts do not imply similar amounts of query work.

## Report the environment

Record build mode, hardware, runtime, GC, compilation settings, worker counts, storage, and enabled features alongside results.

## Related pages

- [Dashboard application](../getting-started/dashboard.md)
- [Rollups and query planning](rollups.md)
- [Ingestion and storage tuning](tuning.md)

---

[Performance](index.md) · [Documentation home](../index.md)

Previous: [Ingestion and storage tuning](tuning.md)
