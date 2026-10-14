---
title: Tracking events
description: Design a collection path for page views, product events, and numeric telemetry.
---

# Tracking events

Design a collection path for page views, product events, and numeric telemetry.

## Choose a collection path

Track directly from .NET, track HTML responses through middleware, or collect browser events through the tracker endpoint. Choose a single page-view source for each visit.

## Design event names and fields

Use stable event names and dimensions that support the questions you want to ask. Keep numeric measurements in values and control dimension cardinality.

## Handle ingestion results

Understand accepted, filtered, rejected, and dropped events. Queue behavior and worker configuration determine how the engine handles load.

## Control visibility

Use ordinary background flushing for live reports. Use explicit flushes when imports, tests, or workflows require a known visibility boundary.

## Related pages

- [Event and report model](../concepts/data-model.md)
- [Extending enrichment](extensibility.md)
- [Ingestion and storage tuning](../performance/tuning.md)

---

[Practical guides](index.md) · [Documentation home](../index.md)

Next: [Web reports](web-reports.md)
