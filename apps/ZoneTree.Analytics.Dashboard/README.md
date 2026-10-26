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
Use `--Analytics:SeedDemo=true` to seed an empty primary site.

## First run

A server without an administrator opens the setup wizard on every page. Once startup output has
settled (in Development, after PureStack's first render), the console ends with a box holding a
setup link and its one-time **setup key**. Open the link, or enter the key, to create the administrator, add your sites and their
domains, choose the reporting time zone and a collection preset, and optionally change any
other setting under **Advanced settings**. The key changes on every start and stops working
after setup. The time zone, data directory, write-ahead-log directory and visitor sketch
precision are stored with the data and cannot change later; everything else can change in the
admin panel.

## Visitor countries

Countries are resolved from visitor IP addresses with the free
[ip-location-db](https://github.com/sapics/ip-location-db) user-country database (PDDL, updated
daily). It is not in the repository; download or update it from the repository root:

```sh
yarn get-ip-location-db
```

The server loads `server/data/ip-location/user-country.mmdb` at startup, so restart it after an
update. Choose another file with `--Dashboard:IpLocationDatabase=<path>`. Without the file, the
startup log says so and countries come only from event dimensions.

## Users and administration

Every page, report and setting requires signing in; only `/_analytics/script.js` and
`/_analytics/event` are public. The dashboard has one administrator. Other users are analysts
(reports and the query explorer) or viewers (reports), each with access to all sites or to
assigned sites. The topbar account menu uses PureStack's sign-in component; its pages are
`/signin/`, `/signout/`, `/account/` and `/account/settings/`.

The administrator's pages under `/admin/` show the engine status and manage sites, users and
their sessions, indexes and rollups, every engine and dashboard setting, and the audit log.
Saving settings first reviews the changes: some apply at once, engine options reopen the engine
for a few seconds, the reporting concurrency limit needs a restart, and changes that delete data
need confirmation. If the engine cannot open with new settings, the previous ones stay in use.

Users, sessions (only SHA-256 hashes of their tokens), settings and the audit log are stored in
one ZoneTree under `server/data/system`; choose another directory with
`--Dashboard:SystemDirectory=<path>`. After setup, these stored settings are the source of
truth for the analytics engine. Passwords are PBKDF2-SHA256 hashes. Session cookies are
HttpOnly and SameSite=Lax, marked Secure over HTTPS, and state-changing requests from other
origins are rejected. Serve the dashboard over HTTPS in production. Behind a reverse proxy that
terminates HTTPS, the server reads `X-Forwarded-Proto`, `X-Forwarded-Host` and `X-Forwarded-For` from
loopback proxies; list other proxy addresses with `--Dashboard:KnownProxies=10.0.0.5,10.0.0.6`.
Failed sign-ins pause a user name from one address, and with a larger limit the address itself.

The host listens on all network interfaces by default. Startup logs include a clickable local
dashboard URL; other devices can connect using `http://<your-computer's-LAN-IP>:5088/`.

For a static release, run `yarn workspace zonetree-analytics-dashboard publish` from the repository root, then start the server with
`--Dashboard:Watch=false`. Its default static directory is `ui/dist/publish`.

The primary engine is configured from the stored settings
([SettingsMapper.cs](server/Settings/SettingsMapper.cs) maps them to `AnalyticsOptions`).
[DashboardAnalyticsOptions.cs](server/DashboardAnalyticsOptions.cs) configures the Development
performance samples, new benchmarks and retained benchmark reports. Local datasets live under
`server/data`. The UI uses the repository's Yarn workspace, `node_modules`, and TypeScript 7.

See the [UI and application guide](ui/README.md) for reports, configuration, dataset tools,
builds and verification. A small integration example lives in
[examples/MinimalHost](../../examples/MinimalHost/README.md).
