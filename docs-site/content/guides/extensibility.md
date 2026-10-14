---
title: Extending enrichment
description: Adapt collection and enrichment to your application's data model.
---

# Extending enrichment

Adapt collection and enrichment to your application's data model.

## Filter before ingestion

Event filters decide which events enter the engine. Use them for application-specific collection rules.

## Enrich events

Enrichers add, normalize, or remove dimensions after built-in enrichment. Keep enrichment consistent with privacy and grouping requirements.

## Replace web classifiers

Provide custom geolocation, user-agent parsing, bot detection, or referrer rules where your application needs different classification.

## Integrate with application code

Depend on `IAnalyticsTracker` where code only records events. Test custom report behavior with an engine on a temporary data directory.

## Related pages

- [Tracking events](tracking.md)
- [Privacy and identity](../concepts/privacy.md)
- [Engine API reference](../reference/engine.md)

---

[Practical guides](index.md) · [Documentation home](../index.md)

Previous: [Sites and tenants](sites-and-tenants.md)
