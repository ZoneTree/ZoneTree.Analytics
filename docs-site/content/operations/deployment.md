---
title: Deployment and lifecycle
description: Run an embedded analytics engine as part of an application you can operate reliably.
---

# Deployment and lifecycle

Run an embedded analytics engine as part of an application you can operate reliably.

## Own the engine lifetime

Use one long-lived engine per data directory. Host registration manages disposal; other applications must arrange shutdown explicitly.

## Provide persistent storage

Keep data directories and configured write-ahead logs on storage that survives application restarts. Directory ownership applies across processes as well as within one process.

## Handle startup and shutdown

Startup may check and backfill indexes. Graceful shutdown drains accepted work and flushes storage; account for both in host lifecycle settings.

## Deploy reporting and the dashboard

Serve reporting endpoints with the host's access controls. Deploy the static dashboard alongside the API or configure its connection to a separate API origin.

## Plan backup and recovery

Treat metadata, data stores, and any separate log directory as parts of the same engine dataset. Define and validate a backup procedure before relying on it.

## Related pages

- [ASP.NET Core integration](../getting-started/aspnet-core.md)
- [Dashboard application](../getting-started/dashboard.md)
- [Observability and troubleshooting](observability.md)

---

[Operations](index.md) · [Documentation home](../index.md)

Previous: [Retention and visitor erasure](retention-and-erasure.md)

Next: [Observability and troubleshooting](observability.md)
