---
title: ZoneTree.Analytics
description: Learn to collect, query, and operate embedded analytics for .NET.
---

# ZoneTree.Analytics documentation

ZoneTree.Analytics brings event collection, website reports, and custom analytics into your .NET application. ZoneTree provides persistent storage; the engine handles enrichment, sessions, aggregation, and query planning.

## Start with your goal

| I want to… | Start here | Continue with |
| --- | --- | --- |
| Track events from .NET | [First events and reports](getting-started/first-events.md) | [Event and report model](concepts/data-model.md) |
| Add analytics to a web application | [ASP.NET Core integration](getting-started/aspnet-core.md) | [Tracking events](guides/tracking.md) |
| Run a complete analytics application | [Dashboard application](getting-started/dashboard.md) | [Web reports](guides/web-reports.md) |
| Build custom reports | [Querying and time series](guides/querying.md) | [Query model reference](reference/query-model.md) |
| Explain or improve query speed | [Rollups and query planning](performance/rollups.md) | [Secondary indexes and backfilling](performance/secondary-indexes.md) |
| Run the engine in production | [Deployment and lifecycle](operations/deployment.md) | [Observability and troubleshooting](operations/observability.md) |

## Explore the documentation

| Area | What you will learn |
| --- | --- |
| [Getting started](getting-started/index.md) | Get from package selection to your first useful report. |
| [Concepts](concepts/index.md) | Understand the engine before making design and configuration decisions. |
| [Practical guides](guides/index.md) | Collect useful data and answer real analytics questions. |
| [Performance](performance/index.md) | Choose aggregation, indexing, and resource settings for your workload. |
| [Operations](operations/index.md) | Configure, deploy, and observe an engine throughout its data lifecycle. |
| [Reference](reference/index.md) | Find the public API and configuration categories in one place. |

## Choose the right part of the project

| Part | Responsibility |
| --- | --- |
| `ZoneTree.Analytics` | Embedded ingestion, storage, queries, and web report models |
| `ZoneTree.Analytics.AspNetCore` | Dependency injection, request and browser tracking, and reporting endpoints |
| Dashboard application | A .NET host and a static PureStack UI using Regor |
| MinimalHost example | A small ASP.NET Core integration to learn from |

## Suggested reading paths

**New to the library:** [Installation](getting-started/installation.md) → [First events and reports](getting-started/first-events.md) → [Architecture](concepts/architecture.md) → [Practical guides](guides/index.md).

**Designing reports:** [Data model](concepts/data-model.md) → [Queries](guides/querying.md) → [Rollups](performance/rollups.md) → [Indexes](performance/secondary-indexes.md).

**Operating an application:** [Configuration](operations/configuration.md) → [Deployment](operations/deployment.md) → [Retention](operations/retention-and-erasure.md) → [Observability](operations/observability.md).
