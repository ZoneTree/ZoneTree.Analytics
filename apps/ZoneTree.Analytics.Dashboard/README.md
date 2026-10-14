# ZoneTree Analytics Dashboard

A multi-page analytics application with a .NET server and a static PureStack UI using Regor.

```text
server/   ZoneTree.Analytics.Dashboard.csproj, APIs, engine configuration and dataset tools
ui/       PureStack pages, Regor components, design, Yarn configuration and frontend tests
```

From the repository root, install the UI dependencies once:

```sh
cd apps/ZoneTree.Analytics.Dashboard/ui
yarn install
```

Then run the server from the repository root:

```sh
dotnet run --project apps/ZoneTree.Analytics.Dashboard/server
```

Open **http://127.0.0.1:5088/**. Development mode starts PureStack automatically, with live
reload for UI changes. The server serves the reporting API and tracker on the same origin.
Use `--Analytics:SeedDemo=true` to seed an empty primary dataset.

For a static release, run `yarn publish` in `ui`, then start the server with
`--Dashboard:Watch=false`. Its default static directory is `ui/dist/publish`.

All engine settings live in [DashboardAnalyticsOptions.cs](server/DashboardAnalyticsOptions.cs).
`ConfigureCommon` applies shared defaults; the per-engine methods configure actual traffic,
performance samples, new benchmarks and retained benchmark reports. Local datasets live
under `server/data`. The UI uses Yarn with `node_modules` and TypeScript 7.

See the [UI and application guide](ui/README.md) for reports, configuration, dataset tools,
builds and verification. A small integration example lives in
[examples/MinimalHost](../../examples/MinimalHost/README.md).
