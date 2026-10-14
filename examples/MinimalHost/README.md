# Minimal analytics host

This example shows engine registration, server-side HTML page tracking, tracker endpoints,
and the reporting API. Dependency injection flushes and disposes the engine on shutdown.

From the repository root:

```sh
dotnet run --project examples/MinimalHost
```

Open **http://127.0.0.1:5090/**. Each HTML visit is tracked; use the link on the page to read
the last hour's summary. Buffered rollups become visible after the engine's flush interval.
Data is stored in `examples/MinimalHost/data/analytics`; override it with
`--Analytics:DataDirectory=<path>`. No Node.js or frontend build is needed.

The complete [dashboard application](../../apps/ZoneTree.Analytics.Dashboard/README.md)
lives under `apps/ZoneTree.Analytics.Dashboard`.
