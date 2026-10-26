import assert from 'node:assert/strict'
import { mkdir, readFile } from 'node:fs/promises'
import { createServer } from 'node:http'
import { extname, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import type { Browser } from 'playwright'
import { chromium } from 'playwright'
import { inject, test } from 'vitest'
import {
  dashboardConfigPath,
  dashboardTrackerPath,
  defaultConfig,
} from '../content/lib/contracts'
import { checkBenchmarkTools } from './benchmark-browser'
import { checkLargeSvgChart } from './chart-browser'

const testUser = {
  id: 'test',
  userName: 'tester',
  displayName: 'Test Administrator',
  role: 'Administrator',
  canQuery: true,
}

test('dashboard browser reports, navigation, and benchmark tools', async () => {
  const workspace = fileURLToPath(new URL('../', import.meta.url))
  const root = resolve(
    workspace,
    process.env.ZT_DASHBOARD_TEST_ROOT || inject('dashboardSiteRoot'),
  )
  const mime: Record<string, string> = {
    '.html': 'text/html',
    '.css': 'text/css',
    '.js': 'text/javascript',
    '.json': 'application/json',
    '.svg': 'image/svg+xml',
    '.png': 'image/png',
    '.woff2': 'font/woff2',
  }
  const server = createServer(async (request, response) => {
    try {
      let path = decodeURIComponent(
        new URL(request.url ?? '/', 'http://localhost').pathname,
      )
      if (path === dashboardTrackerPath) {
        const tracker = await readFile(
          new URL(
            '../../../../src/ZoneTree.Analytics.AspNetCore/tracker.js',
            import.meta.url,
          ),
        )
        response
          .writeHead(200, { 'Content-Type': 'application/javascript' })
          .end(tracker)
        return
      }
      if (path === dashboardConfigPath) {
        response
          .writeHead(200, {
            'Content-Type': 'application/json',
            'Cache-Control': 'no-store',
          })
          .end(
            JSON.stringify({
              ...defaultConfig,
              timeZone: 'UTC',
              user: testUser,
            }),
          )
        return
      }
      if (path === '/_dashboard/api/auth/me') {
        response.writeHead(200, { 'Content-Type': 'application/json' }).end(
          JSON.stringify({
            user: {
              ...testUser,
              allSites: true,
              sites: [],
              disabled: false,
              created: '2026-01-01T00:00:00Z',
              lastSignIn: null,
            },
            sites: defaultConfig.sites,
            sessionId: 'test',
          }),
        )
        return
      }
      if (path === '/_dashboard/api/auth/me/sessions') {
        response
          .writeHead(200, { 'Content-Type': 'application/json' })
          .end('[]')
        return
      }
      if (path.endsWith('/')) path += 'index.html'
      const file = resolve(root, `.${path}`)
      if (relative(root, file).startsWith('..')) {
        response.writeHead(403).end()
        return
      }
      const data = await readFile(file)
      response
        .writeHead(200, {
          'Content-Type': mime[extname(file)] || 'application/octet-stream',
        })
        .end(data)
    } catch {
      response.writeHead(404).end('Not found')
    }
  })
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  assert.ok(
    address && typeof address !== 'string',
    'Test server must listen on a TCP port',
  )
  const base =
    process.env.ZT_DASHBOARD_TEST_URL || `http://127.0.0.1:${address.port}`
  await mkdir(resolve(workspace, 'test-results'), { recursive: true })
  let browser: Browser | undefined
  try {
    browser = await chromium.launch({
      channel: process.env.ZT_BROWSER_CHANNEL || 'chrome',
      headless: true,
    })
    const context = await browser.newContext({
      viewport: { width: 1440, height: 1100 },
      colorScheme: 'light',
      reducedMotion: 'reduce',
      acceptDownloads: true,
    })
    const page = await context.newPage()
    // Keep demo assertions deterministic when the full suite uses a live host.
    await page.route(`**${dashboardConfigPath}`, (route) =>
      route.fulfill({
        json: { ...defaultConfig, timeZone: 'UTC', user: testUser },
      }),
    )
    const errors: string[] = []
    page.on('pageerror', (error) => errors.push(error.message))
    page.on('console', (message) => {
      if (
        (message.type() === 'error' &&
          !/Failed to load resource.*503/.test(message.text())) ||
        /evaluation error|ModelRequiresRef/.test(message.text())
      )
        errors.push(message.text())
    })
    const ready = async () => {
      await page.locator('.page-heading h1').waitFor()
      await page.waitForFunction(
        () => !document.querySelector('.report-loading'),
      )
      assert.deepEqual(errors, [], 'No browser errors while rendering reports')
    }
    await page.goto(`${base}/`)
    await ready()
    assert.equal(
      await page.locator('.api-response-badge').count(),
      0,
      'Demo reports have no API latency badge',
    )
    const tracker = page.locator('#zonetree-tracker')
    assert.equal(await tracker.count(), 1)
    assert.equal(await tracker.getAttribute('defer'), '')
    assert.equal(await tracker.getAttribute('data-allow-localhost'), 'true')
    assert.equal(
      await tracker.evaluate(
        (script: HTMLScriptElement) => new URL(script.src).pathname,
      ),
      dashboardTrackerPath,
    )
    await page.locator('.metric-card').first().waitFor()
    await page.evaluate(() => document.fonts.ready)
    await page.locator('.brand').screenshot({
      path: resolve(workspace, 'test-results/dashboard-logo.png'),
    })
    assert.equal(await page.locator('.metric-card').count(), 5)
    assert.equal(await page.locator('.line-chart').count(), 1)
    assert.equal(await page.locator('.doughnut-chart').count(), 1)
    for (const viewport of [
      { width: 1440, height: 1100 },
      { width: 2560, height: 1100 },
      { width: 390, height: 844 },
      { width: 320, height: 568 },
    ]) {
      await page.setViewportSize(viewport)
      const alignment = await page.evaluate(() => {
        const actions = document.querySelector('.topbar-actions')
        const panels = document.querySelector('.metric-grid')
        if (!actions || !panels)
          throw new Error('Topbar and overview panels must be present')
        return {
          actions: actions.getBoundingClientRect().right,
          panels: panels.getBoundingClientRect().right,
        }
      })
      assert.ok(
        Math.abs(alignment.actions - alignment.panels) <= 1,
        `Topbar actions align with the panels at ${viewport.width}px`,
      )
    }
    await page.setViewportSize({ width: 1440, height: 1100 })
    const checkDoughnutLabel = async () => {
      const labels = await page
        .locator('.doughnut-chart__center text')
        .evaluateAll((elements) =>
          elements.map((element) => {
            const { x, y, width, height } = (
              element as SVGGraphicsElement
            ).getBBox()
            return { text: element.textContent?.trim(), x, y, width, height }
          }),
        )
      assert.equal(labels.length, 2)
      for (const label of labels) {
        assert.ok(
          Math.abs(label.x + label.width / 2 - 50) < 1,
          'Doughnut text stays centered',
        )
        for (const x of [label.x, label.x + label.width])
          for (const y of [label.y, label.y + label.height])
            assert.ok(
              Math.hypot(x - 50, y - 50) < 29,
              `Doughnut label fits inside the hole: ${label.text}`,
            )
      }
    }
    await checkDoughnutLabel()
    assert.match(await page.locator('.metric-value').first().innerText(), /\d/)
    await page
      .locator('.metric-card')
      .filter({
        has: page.locator('.metric-label', { hasText: 'Bounce rate' }),
      })
      .click()
    await page
      .getByRole('heading', { name: 'Bounce rate over time', exact: true })
      .waitFor()
    await page.locator('.chart-data summary').click()
    await page.locator('.chart-data th').first().waitFor()
    assert.deepEqual(await page.locator('.chart-data th').allTextContents(), [
      'Time',
      'Bounce rate',
      'Comparison',
    ])
    assert.match(
      await page.locator('.chart-data td.numeric').first().innerText(),
      /\d.*%/,
    )
    await page.getByRole('button', { name: 'Hourly', exact: true }).click()
    await ready()
    assert.equal(
      await page
        .getByRole('button', { name: 'Hourly', exact: true })
        .getAttribute('aria-pressed'),
      'true',
    )
    assert.equal(
      await page
        .locator('.chart-data')
        .evaluate((element: HTMLDetailsElement) => element.open),
      true,
      'Chart values stay open during a report refresh',
    )
    await page.locator('.chart-data tbody tr').first().waitFor()
    assert.ok(
      (await page.locator('.chart-data tbody tr').count()) > 30,
      'Hourly chart values update after reloading reports',
    )
    await page.getByRole('button', { name: 'Daily', exact: true }).click()
    await ready()
    await page.locator('.metric-card').first().click()
    await page
      .getByRole('heading', { name: 'Unique visitors over time', exact: true })
      .waitFor()
    const topPage = page
      .locator('.overview-grid .breakdown-row')
      .filter({
        has: page.locator('.row-name', { hasText: /^\// }),
      })
      .first()
    const topPagePath = (await topPage.locator('.row-name').innerText()).trim()
    await topPage.click()
    await ready()
    await page
      .getByRole('button', {
        name: `Remove filter page==${topPagePath}`,
        exact: true,
      })
      .click()
    await ready()
    await page.screenshot({
      path: resolve(workspace, 'test-results/overview-desktop.png'),
      fullPage: true,
    })
    await page.getByRole('button', { name: 'Add filter', exact: true }).click()
    await page.getByLabel('Dimension', { exact: true }).selectOption('country')
    await page.getByLabel('Value', { exact: true }).fill('DE')
    await page
      .getByRole('button', { name: 'Apply filter', exact: true })
      .click()
    await ready()
    await page
      .getByRole('button', { name: 'Remove filter country==DE' })
      .waitFor()
    assert.equal(
      await page
        .locator('dialog')
        .evaluate((dialog: HTMLDialogElement) => dialog.open),
      false,
    )
    await page.getByRole('link', { name: 'Pages', exact: true }).click()
    await ready()
    assert.match(page.url(), /\/pages\//)
    await page
      .getByRole('button', { name: 'Remove filter country==DE' })
      .click()
    await ready()
    const download = page.waitForEvent('download')
    await page.getByRole('button', { name: 'Export', exact: true }).click()
    assert.match((await download).suggestedFilename(), /zonetree-pages/)
    for (const [path, title] of [
      ['/acquisition/', 'Acquisition'],
      ['/audience/', 'Audience'],
      ['/events/', 'Events & goals'],
      ['/bots/', 'Bot traffic'],
    ]) {
      await page.goto(base + path)
      await ready()
      assert.equal(await page.locator('.page-heading h1').innerText(), title)
      assert.ok(
        (await page.locator('.data-table tbody tr').count()) > 0,
        `${title} has data`,
      )
    }
    await page.goto(`${base}/explorer/`)
    await ready()
    await page.getByRole('button', { name: 'Run query', exact: true }).click()
    await ready()
    await page
      .getByRole('heading', { name: 'Query results', exact: true })
      .waitFor()
    assert.ok((await page.locator('.data-table tbody tr').count()) > 0)
    await page.goto(`${base}/funnels/`)
    await ready()
    await page.getByRole('button', { name: 'Run funnel', exact: true }).click()
    await ready()
    await page
      .getByRole('heading', { name: 'Conversion journey', exact: true })
      .waitFor()
    assert.equal(await page.locator('.funnel-result').count(), 3)
    // The signed-in administrator sees their initials, the administration navigation and their account.
    assert.equal(
      await page.locator('[data-user-initials]').first().textContent(),
      'TA',
    )
    assert.ok(await page.locator('.nav-admin').isVisible())
    await page.goto(`${base}/account/`)
    await page
      .getByRole('heading', { name: 'Signed-in browsers', exact: true })
      .waitFor()
    await page.goto(`${base}/`)
    await ready()
    await page.getByRole('button', { name: 'Dark theme', exact: true }).click()
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark')
    await page.screenshot({
      path: resolve(workspace, 'test-results/overview-dark.png'),
      fullPage: true,
    })
    await page.setViewportSize({ width: 390, height: 844 })
    await page.waitForFunction(() => {
      const sidebar = document.querySelector('.sidebar')
      return sidebar !== null && sidebar.getBoundingClientRect().right <= 0
    })
    await page.screenshot({
      path: resolve(workspace, 'test-results/overview-mobile.png'),
      fullPage: true,
    })
    assert.ok(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
      'Mobile page must not overflow',
    )
    const menu = page.locator('.topbar label[for="nav-toggle"]')
    const closeMenu = page.locator('.drawer-close')
    await menu.click()
    await page.waitForFunction(() => {
      const sidebar = document.querySelector('.sidebar')
      return (
        sidebar !== null &&
        Math.round(sidebar.getBoundingClientRect().left) === 0
      )
    })
    assert.equal(await page.locator('#nav-toggle').isChecked(), true)
    await page.screenshot({
      path: resolve(workspace, 'test-results/mobile-navigation.png'),
    })
    for (const viewport of [
      { width: 390, height: 844 },
      { width: 320, height: 568 },
      { width: 780, height: 390 },
    ]) {
      await page.setViewportSize(viewport)
      assert.equal(
        await page.locator('.sidebar .dashboard-logo').evaluate((logo) => {
          const rect = logo.getBoundingClientRect()
          return [
            rect.top + 2,
            rect.top + rect.height / 2,
            rect.bottom - 2,
          ].every((y) =>
            logo.contains(
              document.elementFromPoint(rect.left + rect.width / 2, y),
            ),
          )
        }),
        true,
        `The mobile logo is unobstructed at ${viewport.width}x${viewport.height}`,
      )
      assert.equal(
        await closeMenu.evaluate((toggle) => {
          const rect = toggle.getBoundingClientRect()
          return toggle.contains(
            document.elementFromPoint(
              rect.left + rect.width / 2,
              rect.top + rect.height / 2,
            ),
          )
        }),
        true,
        'The drawer close control remains accessible while the drawer is open',
      )
      const sidebar = page.locator('.sidebar')
      assert.equal(
        await sidebar.evaluate(
          (element) => element.getBoundingClientRect().top,
        ),
        0,
        'Navigation starts at the top of the viewport',
      )
      assert.equal(
        await sidebar.evaluate(
          (element) => element.getBoundingClientRect().height,
        ),
        viewport.height,
        'Navigation fills the viewport height',
      )
      assert.equal(
        await sidebar.evaluate(
          (element) => getComputedStyle(element).scrollbarWidth,
        ),
        'none',
      )
      await sidebar.evaluate((element) => {
        element.scrollTop = element.scrollHeight
      })
      assert.ok(
        await sidebar.evaluate((element) => element.scrollTop > 0),
        'Short-screen navigation remains scrollable',
      )
      await sidebar.evaluate((element) => {
        element.scrollTop = 0
      })
    }
    await page.setViewportSize({ width: 390, height: 844 })
    assert.equal(
      await page
        .locator('.sidebar')
        .evaluate((element) =>
          Math.round(element.getBoundingClientRect().left),
        ),
      0,
    )
    await closeMenu.click()
    await page.setViewportSize({ width: 1440, height: 1100 })

    // A failed live connection must never fall back to plausible demo numbers.
    await page.evaluate(() => {
      sessionStorage.removeItem('zt-dashboard-scope-v1')
    })
    await page.route(`**${dashboardConfigPath}`, (route) =>
      route.fulfill({
        status: 503,
        json: { error: 'Configuration is unavailable.' },
      }),
    )
    await page.goto(`${base}/`)
    await ready()
    await page
      .getByText(/Cannot load dashboard configuration \(503\)/)
      .waitFor()
    assert.equal(await page.locator('.metric-card').count(), 0)
    await page.unroute(`**${dashboardConfigPath}`)
    await page.route(`**${dashboardConfigPath}`, (route) =>
      route.fulfill({
        json: {
          mode: 'live',
          apiBaseUrl: '/mock-analytics',
          timeZone: 'UTC',
          sites: [{ id: '', name: 'Test site' }],
          refreshSeconds: 0,
        },
      }),
    )
    await page.route('**/mock-analytics/**', (route) =>
      route.fulfill({
        status: 503,
        json: { error: 'The reporting API is busy. Try again later.' },
      }),
    )
    await page.goto(`${base}/`)
    await ready()
    await page
      .getByText('The reporting API is busy. Try again later.', { exact: true })
      .waitFor()
    assert.equal(await page.locator('.metric-card').count(), 0)
    assert.equal(
      await page
        .getByRole('button', { name: 'Export', exact: true })
        .isDisabled(),
      true,
    )
    await page.unroute('**/mock-analytics/**')
    const emptySummary = {
      visitors: 0,
      pageViews: 0,
      sessions: 0,
      bounceRate: 0,
      averageSessionDuration: 0,
      pageViewsPerSession: 0,
      botHits: null,
    }
    const reportBase = {
      from: '2026-10-01T00:00:00Z',
      to: '2026-10-02T00:00:00Z',
      plan: { complete: true, warnings: [], steps: [], rollupSuggestions: [] },
    }
    const trackedPath = '<img src=x onerror="window.pwned=1">{{window.pwned=2}}'
    let deviceViews = 2_212_963
    await page.route('**/mock-analytics/**', (route) => {
      const url = new URL(route.request().url())
      const endpoint = url.pathname.split('/').pop()
      if (endpoint === 'summary')
        return route.fulfill({ json: { ...reportBase, summary: emptySummary } })
      if (endpoint === 'realtime')
        return route.fulfill({ json: { visitors: 0, minutes: 5 } })
      if (
        endpoint === 'breakdown' &&
        url.searchParams.get('dimension') === 'device'
      )
        return route.fulfill({
          json: {
            ...reportBase,
            totalRows: 1,
            rows: [
              { value: 'Desktop', ...emptySummary, pageViews: deviceViews },
            ],
          },
        })
      if (endpoint === 'pages') {
        const offset = Number(url.searchParams.get('offset') || 0)
        const rows = Array.from({ length: 41 }, (_, index) => ({
          page: index === 0 ? trackedPath : `/page-${index}`,
          visitors: 1,
          pageViews: 1,
          entrances: 0,
          exits: 0,
          exitRate: 0,
          botHits: null,
        }))
        return route.fulfill({
          json: {
            ...reportBase,
            totalRows: rows.length,
            rows: rows.slice(offset, offset + 20),
          },
        })
      }
      return route.fulfill({ json: { ...reportBase, totalRows: 0, rows: [] } })
    })
    await page
      .getByRole('button', { name: 'Refresh reports', exact: true })
      .click()
    await ready()
    assert.equal(await page.locator('.metric-value').first().innerText(), '0')
    await page.locator('.api-response-badge').waitFor()
    assert.match(
      await page.locator('.api-response-badge').innerText(),
      /^\d+ ms$/,
    )
    assert.equal(
      await page.locator('.api-response-badge + .mode-badge').innerText(),
      'Live API',
    )
    assert.equal(await page.locator('.live-count strong').innerText(), '0')
    assert.equal(
      await page
        .locator('.doughnut-chart__center-value')
        .textContent()
        .then((text) => text?.trim()),
      '2,212,963',
    )
    await checkDoughnutLabel()
    deviceViews = Number.MAX_SAFE_INTEGER
    await page
      .getByRole('button', { name: 'Refresh reports', exact: true })
      .click()
    await ready()
    await checkDoughnutLabel()
    await page.setViewportSize({ width: 390, height: 844 })
    await checkDoughnutLabel()
    await page.setViewportSize({ width: 1440, height: 1100 })
    await page.goto(`${base}/pages/`)
    await ready()
    await page.getByText(trackedPath, { exact: true }).waitFor()
    assert.equal(await page.locator('img[src="x"]').count(), 0)
    assert.equal(
      await page.evaluate(() => (window as Window & { pwned?: number }).pwned),
      undefined,
    )
    assert.equal(await page.locator('.data-table tbody tr').count(), 20)
    await page.getByRole('button', { name: 'Next →', exact: true }).click()
    await ready()
    assert.equal(await page.locator('.data-table tbody tr').count(), 20)
    await page.getByText('/page-20', { exact: true }).waitFor()
    await page.getByRole('button', { name: 'Next →', exact: true }).click()
    await ready()
    await page.getByText('/page-40', { exact: true }).waitFor()
    assert.equal(await page.locator('.data-table tbody tr').count(), 1)
    assert.equal(
      await page
        .getByRole('button', { name: 'Next →', exact: true })
        .isDisabled(),
      true,
    )
    let releaseRefresh: (() => void) | undefined
    const refreshGate = new Promise<void>((resolve) => {
      releaseRefresh = resolve
    })
    const previousTable = await page.locator('.data-table').innerHTML()
    const holdRefresh = async (route: import('playwright').Route) => {
      await refreshGate
      await route.fallback()
    }
    await page.route('**/mock-analytics/**', holdRefresh)
    try {
      await page
        .getByRole('button', { name: 'Refresh reports', exact: true })
        .click()
      await page.locator('.page-heading .report-loading').waitFor()
      assert.equal(
        await page.locator('.data-table').innerHTML(),
        previousTable,
        'Previous table stays visible while refreshing',
      )
      assert.equal(await page.locator('.loading-panel').count(), 0)
      assert.equal(
        await page
          .getByRole('button', { name: 'Export', exact: true })
          .isDisabled(),
        true,
      )
    } finally {
      releaseRefresh?.()
      await ready()
      await page.unroute('**/mock-analytics/**', holdRefresh)
    }
    const failedRefresh = (route: import('playwright').Route) =>
      route.fulfill({
        status: 503,
        json: {
          error: 'Refresh failed; previous results are still available.',
        },
      })
    await page.route('**/mock-analytics/**', failedRefresh)
    await page
      .getByRole('button', { name: 'Refresh reports', exact: true })
      .click()
    await ready()
    await page
      .getByText('Refresh failed; previous results are still available.', {
        exact: true,
      })
      .waitFor()
    assert.equal(
      await page.locator('.data-table').innerHTML(),
      previousTable,
      'A failed refresh retains the previous report',
    )
    assert.equal(
      await page
        .getByRole('button', { name: 'Export', exact: true })
        .isDisabled(),
      true,
    )
    await page.unroute('**/mock-analytics/**', failedRefresh)
    assert.deepEqual(errors, [], 'No browser errors')
    await checkLargeSvgChart(page, base, reportBase, emptySummary)
    assert.deepEqual(errors, [], 'No browser errors with large chart responses')
    await checkBenchmarkTools(page, base)
    assert.deepEqual(errors, [], 'No browser errors with benchmark tools')
    console.log(
      'Browser checks passed: nine routes, filters, charts, queries, funnels, account, CSV, themes, mobile navigation, API failure/recovery, zero counts, safe text binding, pagination.',
    )
  } finally {
    await browser?.close()
    await new Promise<void>((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    )
  }
})
