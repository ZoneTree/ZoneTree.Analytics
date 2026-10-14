---
title: Configuration
description: Organize engine and host settings so their effects are easy to reason about.
---

# Configuration

Organize engine and host settings so their effects are easy to reason about.

## Separate engine and host options

`AnalyticsOptions` configures the core engine. `AnalyticsWebOptions` configures ASP.NET Core collection and reporting integration.

## Configure before opening

The engine copies its options on creation. Apply changes through the configuration used to open the next engine instance.

## Recognize persisted choices

Some choices, including reporting time zone and write-ahead-log location, are persisted with the data directory. Treat them differently from runtime tuning values.

## Keep multiple engines consistent

Centralize common settings when an application opens multiple engines. The dashboard's [shared options](../../apps/ZoneTree.Analytics.Dashboard/server/DashboardAnalyticsOptions.cs) are one example.

## Plan index changes

New index definitions can require startup backfills. Consider available history, startup duration, and storage growth when changing them.

## Related pages

- [Configuration reference](../reference/configuration.md)
- [Secondary indexes and backfilling](../performance/secondary-indexes.md)
- [Deployment and lifecycle](deployment.md)

---

[Operations](index.md) · [Documentation home](../index.md)

Next: [Retention and visitor erasure](retention-and-erasure.md)
