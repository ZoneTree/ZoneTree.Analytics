---
title: ASP.NET Core integration
description: Connect the engine to application hosting, request tracking, and reporting endpoints.
---

# ASP.NET Core integration

Connect the engine to application hosting, request tracking, and reporting endpoints.

## Register the engine

`AddZoneTreeAnalytics` registers the engine and `IAnalyticsTracker` with dependency injection. Hosting manages the engine lifetime and resolves relative data paths against the content root.

## Choose page-view tracking

Use server-side middleware or the browser tracker for page views. Enabling both for the same visits records duplicate page views.

## Expose tracking and reports

`MapZoneTreeAnalytics` exposes the tracker script and event endpoint. `MapZoneTreeAnalyticsApi` exposes reporting routes.

## Connect application identity

Choose site and visitor resolvers, request exclusions, and client-event rules for your application. Apply host authorization to reporting access.

## Start with the minimal example

The [MinimalHost example](../../examples/MinimalHost/README.md) shows a small server-side integration without the dashboard build.

## Related pages

- [Tracking events](../guides/tracking.md)
- [Sites and tenants](../guides/sites-and-tenants.md)
- [HTTP API](../reference/http-api.md)

---

[Getting started](index.md) · [Documentation home](../index.md)

Previous: [First events and reports](first-events.md)

Next: [Dashboard application](dashboard.md)
