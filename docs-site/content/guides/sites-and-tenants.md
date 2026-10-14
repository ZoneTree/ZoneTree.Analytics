---
title: Sites and tenants
description: Organize multiple websites, applications, or tenants within an analytics host.
---

# Sites and tenants

Organize multiple websites, applications, or tenants within an analytics host.

## Choose site identifiers

Assign stable site IDs to events and reports. The default site handles requests that do not specify one.

## Resolve sites at collection time

ASP.NET Core integration can resolve a site from the request. Direct ingestion can assign a site explicitly.

## Authorize reporting access

Data separation by site is part of the query model. The host must authorize which sites each caller may access.

## Choose engine boundaries

Multiple sites can share an engine. Separate engines require separate data directories and independent configuration, maintenance, and resource budgets.

## Related pages

- [ASP.NET Core integration](../getting-started/aspnet-core.md)
- [Deployment and lifecycle](../operations/deployment.md)
- [HTTP API](../reference/http-api.md)

---

[Practical guides](index.md) · [Documentation home](../index.md)

Previous: [Segments and funnels](segments-and-funnels.md)

Next: [Extending enrichment](extensibility.md)
