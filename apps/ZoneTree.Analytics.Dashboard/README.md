# ZoneTree Analytics Dashboard

A multi-page analytics application with a .NET server and a static PureStack UI using Regor.

```text
server/   ZoneTree.Analytics.Dashboard.csproj, APIs, engine configuration and dataset tools
ui/       PureStack pages, Regor components, design and frontend tests
```

From the repository root, install the shared workspace dependencies once:

```sh
yarn install
```

Then run the server from the repository root:

```sh
yarn dashboard
```

Open **http://localhost:5088/**. Development mode starts PureStack automatically, with live
reload for UI changes. The server serves the reporting API and tracker on the same origin.
Use `--Analytics:SeedDemo=true` to seed an empty primary dataset.

The host listens on all network interfaces by default. Startup logs include a clickable local
dashboard URL; other devices can connect using `http://<your-computer's-LAN-IP>:5088/`.

For a static release, run `yarn workspace zonetree-analytics-dashboard publish` from the repository root, then start the server with
`--Dashboard:Watch=false`. Its default static directory is `ui/dist/publish`.

All engine settings live in [DashboardAnalyticsOptions.cs](server/DashboardAnalyticsOptions.cs).
`ConfigureCommon` applies shared defaults; the per-engine methods configure actual traffic,
performance samples, new benchmarks and retained benchmark reports. Local datasets live
under `server/data`. The UI uses the repository's Yarn workspace, `node_modules`, and TypeScript 7.

See the [UI and application guide](ui/README.md) for reports, configuration, dataset tools,
builds and verification. A small integration example lives in
[examples/MinimalHost](../../examples/MinimalHost/README.md).
