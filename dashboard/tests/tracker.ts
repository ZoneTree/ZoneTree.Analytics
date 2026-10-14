import assert from 'node:assert/strict'
import { setTimeout as pause } from 'node:timers/promises'
import { chromium, request } from 'playwright'
import { validateConfig } from '../content/lib/config'
import type { SummaryReport } from '../content/lib/contracts'
import { dashboardConfigPath, dashboardEventPath } from '../content/lib/contracts'

const base = new URL(process.env.ZT_DASHBOARD_TEST_URL ?? 'http://127.0.0.1:5088/')
const http = await request.newContext({ baseURL: base.origin })
const browser = await chromium.launch({ channel: process.env.ZT_BROWSER_CHANNEL ?? 'chrome', headless: true })
try {
  const response = await http.get(dashboardConfigPath)
  assert.ok(response.ok(), 'The host must expose dashboard configuration')
  const config = validateConfig(await response.json())
  assert.equal(config.mode, 'live', 'Run tracker checks against the .NET sample host')
  const summary = async () => {
    const result = await http.get(`${config.apiBaseUrl}/summary`, {
      params: { range: 'today', tz: config.timeZone, site: '' },
    })
    assert.ok(result.ok())
    return (await result.json() as SummaryReport).summary
  }
  const before = await summary()
  const context = await browser.newContext({
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36',
  })
  // Simulate a human browser for this integration check; normal QA keeps automation excluded.
  await context.addInitScript(() => Object.defineProperty(navigator, 'webdriver', { get: () => false }))
  const page = await context.newPage()
  for (const path of ['', 'pages/']) {
    const target = new URL(path, base)
    const beacon = page.waitForResponse((result) => {
      const url = new URL(result.url())
      return url.pathname === dashboardEventPath && result.request().method() === 'POST'
    })
    await page.goto(target.href)
    const accepted = await beacon
    assert.equal(accepted.status(), 202, 'The real engine must accept the page-view beacon')
    const payload = JSON.parse(accepted.request().postData() ?? '{}') as { n: string; u: string }
    assert.equal(payload.n, 'pageview')
    assert.equal(new URL(payload.u).pathname, target.pathname)
    await page.locator('.page-heading h1').waitFor()
  }
  let after = await summary()
  for (let attempt = 0; attempt < 30 && after.pageViews < before.pageViews + 2; attempt++) {
    await pause(100)
    after = await summary()
  }
  assert.ok(after.pageViews >= before.pageViews + 2, 'Dashboard visits must increase real page views')
  console.log(`Tracker checks passed: dashboard navigation sent accepted beacons; page views increased from ${before.pageViews} to ${after.pageViews}.`)
} finally {
  await browser.close()
  await http.dispose()
}
