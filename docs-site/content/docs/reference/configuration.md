---
title: Configuration reference
description: Provide one lookup location for core, storage, and integration settings.
---

# Configuration reference

Provide one lookup location for core, storage, and integration settings.

## Core option groups

Organize `AnalyticsOptions` into ingestion, sessions, rollups, queries, web enrichment, privacy, retention, and storage.

## Rollups and indexes

Cover rollup definitions, optional aggregates, dimension indexes, rollup index combinations, and their historical coverage requirements.

## ZoneTree storage settings

Cover per-store options, partition targets, write-ahead logs, idle partition handling, and logging.

## ASP.NET Core options

Cover tracker paths, site and visitor resolvers, collection rules, payload and reporting limits, and site authorization.

## Configuration semantics

For each setting, document its default, unit, allowed values, dependencies, and whether reopening or a fresh data directory is required. Keep dashboard-specific defaults clearly identified.

## Related pages

- [Configuration guide](../operations/configuration.md)
- [Ingestion and storage tuning](../performance/tuning.md)
- [HTTP API](http-api.md)

---

[Reference](index.md) · [Documentation home](../index.md)

Previous: [Query model reference](query-model.md)

Next: [HTTP API reference](http-api.md)
