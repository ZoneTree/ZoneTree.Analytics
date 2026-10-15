# ZoneTree Analytics website

PureStack builds the product landing page and library documentation from `content`.
The public site starts at `/`; the documentation lives under `/docs/`.

## Local legal content

Each `/legal/` page imports its notice from its own Git-ignored `_private/`
folder: `content/legal/imprint/_private/imprint.mdx` and
`content/legal/privacy/_private/privacy.mdx`. Restore those files from your
private copy when setting up another checkout or a publishing environment.
The public page wrappers contain no operator details.

## Develop

Requires Node.js 22.17 or newer and Corepack-enabled Yarn. The root package manages this site and the dashboard as workspaces with one lockfile. From the repository root:

```sh
yarn install
yarn docs:dev
```

The watcher serves the site on port 4900. Yarn uses `node_modules` with lifecycle
scripts disabled. TypeScript checks use major version 7.

## Validate and publish static files

From the repository root:

```sh
yarn check
yarn workspace analytics.zonetree.dev publish
```

Publishing generates local files in `docs-site/dist/web`, including Pagefind search,
sitemap, robots, and PureStack's generated favicon. Deployment is a separate action.

Biome configuration, linting, and formatting are shared at the repository root. Run
`yarn lint` or `yarn format:check` to check files, and `yarn lint:fix` or `yarn format`
to apply fixes. `yarn check` includes both checks.

## Content conventions

- Use MDX for pages, with title, description, and explicit navigation order.
- Keep page links, imported examples, components, and images inside `content`.
- Place each named page in its own folder, such as `querying/querying.mdx`.
- Keep imported examples, media, and partials beside the page that uses them.
- Keep folder index pages as `index.mdx` in the directory they describe.
- Show code through `<import-codeblock>` with a local source such as `./query.cs`.
- Use SVG for diagrams and real dashboard captures for product screenshots.
- Use PureStack components for layout, tabs, callouts, navigation, and search.
- Keep custom Regor templates and logic together and export component interfaces.
- Keep library defaults distinct from dashboard-specific configuration.

The theme and shared components are in `content/ui`. The skin follows the Regor
documentation site's chartreuse, warm neutral, and graphite palettes with system fonts.
