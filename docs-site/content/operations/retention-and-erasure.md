---
title: Retention and visitor erasure
description: Manage historical coverage and the removal of stored analytics data.
---

# Retention and visitor erasure

Manage historical coverage and the removal of stored analytics data.

## Choose history by tier

Raw events and minute, hourly, and daily rollups have separate retention policies. Keep enough event history for ad-hoc queries, behavioral analysis, and required backfills.

## Understand expiry

Queries hide expired records according to retention cutoffs. Background maintenance and merges reclaim physical storage on their own schedule.

## Erase visitor data

Visitor erasure requires the event history needed to identify and remove contributions. Plan its behavior together with raw-event retention and identity policy.

## Interpret historical reports

A date range can extend beyond available data. Use report warnings and plan completeness rather than treating every empty historical result as zero activity.

## Related pages

- [Privacy and identity](../concepts/privacy.md)
- [Time, freshness, and accuracy](../concepts/time-and-accuracy.md)
- [Engine API reference](../reference/engine.md)

---

[Operations](index.md) · [Documentation home](../index.md)

Previous: [Configuration](configuration.md)

Next: [Deployment and lifecycle](deployment.md)
