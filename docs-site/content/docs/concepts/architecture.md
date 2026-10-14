---
title: Engine architecture
description: Understand the path from an accepted event to a queryable analytics result.
---

# Engine architecture

Understand the path from an accepted event to a queryable analytics result.

## From events to reports

```
flowchart LR
    Events["Application events"] --> Ingestion["Background ingestion"]
    Ingestion --> History["Event history and sessions"]
    Ingestion --> Rollups["Configured rollups"]
    History --> Queries["Query planner and execution"]
    Rollups --> Queries
    Queries --> Reports["Reports and custom results"]
```

Optional secondary indexes provide ordered access to copied event data and rollup rows.

## Ingestion pipeline

Tracking validates and queues events. Background workers enrich events, identify visitors and sessions, write stored events, and aggregate configured rollups.

## Storage model

ZoneTree stores ordered persistent data for events, sessions, rollups, and optional indexes. Metadata records engine configuration and coverage information.

## Query execution

The planner chooses eligible rollups, indexed reads, and raw-event scans for each part of a request. A report can combine multiple sources.

## Lifetime and ownership

One engine owns one data directory. The application controls its lifetime; background ingestion and storage maintenance run within that lifetime.

## Related pages

- [Event and report model](data-model.md)
- [Rollups and query planning](../performance/rollups.md)
- [Deployment and lifecycle](../operations/deployment.md)

---

[Concepts](index.md) · [Documentation home](../index.md)

Next: [Event and report model](data-model.md)
