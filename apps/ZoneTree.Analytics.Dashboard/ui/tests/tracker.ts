import assert from 'node:assert/strict'
import { setTimeout as pause } from 'node:timers/promises'
import type { Browser } from 'playwright'
import { chromium, request } from 'playwright'
import { inject, test, vi } from 'vitest'
import { validateConfig } from '../content/lib/config'
import type { Page, Report, SummaryReport } from '../content/lib/contracts'
import {
  dashboardConfigPath,
  dashboardEventPath,
} from '../content/lib/contracts'

test('dashboard visits record accepted beacons in the live .NET engine', async () => {
  const base = new URL(inject('dashboardTestUrl'))
  const http = await request.newContext({ baseURL: base.origin })
  let browser: Browser | undefined
  try {
    browser = await chromium.launch({
      channel: process.env.ZT_BROWSER_CHANNEL ?? 'chrome',
      headless: true,
    })
    // A fresh host starts in setup mode: create the administrator and record dashboard visits.
    const verified = await http.post('/_dashboard/api/setup/verify', {
      data: { key: inject('dashboardSetupKey') },
    })
    assert.ok(
      verified.ok(),
      'The setup key from the host log must unlock setup',
    )
    const { settings } = (await verified.json()) as {
      settings: { sites: { trackDashboardUsage: boolean } }
    }
    settings.sites.trackDashboardUsage = true
    const completed = await http.post('/_dashboard/api/setup/complete', {
      data: {
        key: inject('dashboardSetupKey'),
        administrator: {
          userName: 'tester',
          password: 'tracker test password',
        },
        settings,
      },
    })
    assert.ok(completed.ok(), await completed.text())
    const response = await http.get(dashboardConfigPath)
    assert.ok(response.ok(), 'The host must expose dashboard configuration')
    const config = validateConfig(await response.json())
    assert.equal(
      config.mode,
      'live',
      'Run tracker checks against the .NET sample host',
    )
    const summary = async () => {
      const result = await http.get(`${config.apiBaseUrl}/summary`, {
        params: {
          range: 'today',
          tz: config.timeZone,
          site: 'dashboard-usage',
        },
      })
      assert.ok(result.ok())
      return ((await result.json()) as SummaryReport).summary
    }
    const before = await summary()
    const context = await browser.newContext({
      storageState: await http.storageState(),
      userAgent:
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36',
    })
    // Simulate a human browser for this integration check; normal QA keeps automation excluded.
    await context.addInitScript(() =>
      Object.defineProperty(navigator, 'webdriver', { get: () => false }),
    )
    const page = await context.newPage()
    assert.ok(config.liveUrl, 'The live host must advertise its WebSocket')
    const reportPath = `${new URL(config.apiBaseUrl, base).pathname}/`
    const reportRequests: string[] = []
    page.on('request', (request) => {
      const { pathname } = new URL(request.url())
      if (pathname.startsWith(reportPath) && pathname !== `${reportPath}config`)
        reportRequests.push(pathname)
    })
    await page.route('**/realtime?*', (route) =>
      route.fulfill({ json: { visitors: 900, minutes: 5 } }),
    )
    for (const path of ['', 'pages/']) {
      const target = new URL(path, base)
      target.searchParams.set('site', 'dashboard-usage')
      target.searchParams.set('range', 'today')
      const socket = page.waitForEvent(
        'websocket',
        (connection) => new URL(connection.url()).pathname === config.liveUrl,
      )
      const beacon = page.waitForResponse((result) => {
        const url = new URL(result.url())
        return (
          url.pathname === dashboardEventPath &&
          result.request().method() === 'POST'
        )
      })
      await page.goto(target.href)
      await socket
      const accepted = await beacon
      assert.equal(
        accepted.status(),
        202,
        'The real engine must accept the page-view beacon',
      )
      // Chromium does not expose sendBeacon Blob bodies through postData().
      // The tracker unit tests check payloads; this test checks server ingestion.
      await page.locator('.page-heading h1').waitFor()
      if (!path) {
        await page.waitForFunction(() => {
          const visitors =
            document.querySelector('.live-count strong')?.textContent
          return (
            /^\d+$/.test(visitors?.trim() ?? '') && visitors?.trim() !== '900'
          )
        })
        await page.locator('#overview-app [aria-busy="false"]').waitFor()
        assert.equal(reportRequests.length, 7, reportRequests.join(', '))
      }
    }
    // The realtime HTTP response above is a sentinel. The actual number must
    // come from the authenticated reporting stream.
    await page.waitForFunction(
      () =>
        document.querySelector('.live-count strong')?.textContent?.trim() ===
        '1',
      undefined,
      { timeout: 45_000 },
    )
    let after = await summary()
    for (
      let attempt = 0;
      attempt < 30 && after.pageViews < before.pageViews + 2;
      attempt++
    ) {
      await pause(100)
      after = await summary()
    }
    assert.ok(
      after.pageViews >= before.pageViews + 2,
      'Dashboard visits must increase real page views',
    )

    const pageKey = '/blog/%E5%BA%94%E7%94%A8%2Ftips'
    const pageLabel = '/blog/应用%2Ftips'
    const beaconStatus = await page.evaluate(
      async ({ endpoint, path }) => {
        const response = await fetch(endpoint, {
          method: 'POST',
          headers: { 'Content-Type': 'text/plain' },
          body: JSON.stringify({ n: 'pageview', u: location.origin + path }),
        })
        return response.status
      },
      { endpoint: dashboardEventPath, path: pageKey },
    )
    assert.equal(beaconStatus, 202)
    await vi.waitFor(
      async () => {
        const result = await http.get(`${config.apiBaseUrl}/pages`, {
          params: {
            site: 'dashboard-usage',
            range: 'today',
            tz: config.timeZone,
          },
        })
        assert.ok(result.ok())
        const pages = (await result.json()) as Report<Page>
        assert.ok(pages.rows.some((row) => row.page === pageKey))
      },
      { timeout: 10_000, interval: 200 },
    )
    await page.goto(new URL('?site=dashboard-usage&range=today', base).href)
    await page
      .locator('.row-name')
      .getByText(pageLabel, { exact: true })
      .waitFor()
    await page.goto(
      new URL('pages/?site=dashboard-usage&range=today', base).href,
    )
    const filtered = page.waitForRequest((request) => {
      const url = new URL(request.url())
      return (
        url.pathname === `${reportPath}pages` &&
        url.searchParams.getAll('filter').includes(`page==${pageKey}`)
      )
    })
    await page.getByRole('button', { name: pageLabel, exact: true }).click()
    await filtered
    await page
      .getByRole('button', { name: `Remove filter page==${pageLabel}` })
      .waitFor()

    console.log(
      `Tracker checks passed: dashboard navigation sent accepted beacons; page views increased from ${before.pageViews} to ${after.pageViews}.`,
    )
  } finally {
    await browser?.close()
    await http.dispose()
  }
})
