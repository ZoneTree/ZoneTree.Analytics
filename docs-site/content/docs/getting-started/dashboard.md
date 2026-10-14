---
title: Dashboard application
description: Run the included analytics product and connect it to your engine.
---

# Dashboard application

Run the included analytics product and connect it to your engine.

## Understand the application

The dashboard combines a .NET server with a static PureStack UI built with Regor. Its source lives in `apps/ZoneTree.Analytics.Dashboard/server` and `ui`.

## Run in development

The server starts the PureStack watcher in Development mode. UI dependencies use Yarn with `node_modules`; TypeScript tooling stays on major version 7.

## Connect reports

The application serves reporting APIs and connection settings. The UI offers overview, page, acquisition, audience, event, bot, funnel, and custom-query reports.

## Explore sample datasets

Development tools can generate performance samples and run ingestion benchmarks. Their datasets use separate engines and directories from primary website traffic.

## Build a static release

Generate the UI with PureStack and serve its published files with the application. See the [application guide](../../apps/ZoneTree.Analytics.Dashboard/README.md) for current commands.

## Related pages

- [Deployment and lifecycle](../operations/deployment.md)
- [Benchmarking](../performance/benchmarking.md)
- [Web reports](../guides/web-reports.md)

---

[Getting started](index.md) · [Documentation home](../index.md)

Previous: [ASP.NET Core integration](aspnet-core.md)
