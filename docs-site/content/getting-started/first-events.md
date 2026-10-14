---
title: First events and reports
description: Follow the smallest complete path from opening an engine to reading a report.
---

# First events and reports

Follow the smallest complete path from opening an engine to reading a report.

## Open one engine

Create an `AnalyticsEngine` for the application lifetime. Configure its data directory and reporting time zone before opening it.

## Track an event

Begin with a page view or a named custom event. Attach dimensions for categories and numeric values for measurements.

## Make tracked events visible

Tracking queues work for background ingestion. Use `FlushAsync` when the next query must include all events accepted before the flush.

## Read a report

Use `analytics.Web` for common website reports or `AnalyticsQuery` for custom analytics. Inspect the returned plan alongside the data.

## Close the engine

Dispose the engine during application shutdown so queued work is drained and stored data is flushed.

## Related pages

- [Event and report model](../concepts/data-model.md)
- [Tracking events](../guides/tracking.md)
- [Web reports](../guides/web-reports.md)

---

[Getting started](index.md) · [Documentation home](../index.md)

Previous: [Installation](installation.md)

Next: [ASP.NET Core integration](aspnet-core.md)
