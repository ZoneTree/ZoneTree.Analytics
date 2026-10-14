import assert from 'node:assert/strict'
import type { Page, Route } from 'playwright'
import type { Point, Report, Summary } from '../content/lib/contracts'
import { chartPointBudget } from '../content/overview/chart-sampling'

export async function checkLargeSvgChart(
  page: Page,
  base: string,
  reportBase: Pick<Report<Point>, 'from' | 'to' | 'plan'>,
  summary: Summary,
) {
  const size = 100_000
  const start = Date.parse(reportBase.from)
  const rows: Point[] = Array.from({ length: size }, (_, i) => ({
    bucket: new Date(start + i * 60_000).toISOString(),
    summary: {
      ...summary,
      visitors: i === 12_345 ? 10_000 : 10 + Math.floor(5 * Math.sin(i / 13)),
      previous: {
        ...summary,
        visitors: i === 76_543 ? 9000 : 10 + Math.floor(5 * Math.cos(i / 17)),
      },
    },
  }))
  const report = {
    ...reportBase,
    to: new Date(start + size * 60_000).toISOString(),
  }
  const payload = JSON.stringify({ ...report, totalRows: size, rows })
  let timeseriesPayload = payload
  await page.unroute('**/mock-analytics/**')
  await page.route('**/mock-analytics/**', (route) => {
    const endpoint = new URL(route.request().url()).pathname.split('/').pop()
    if (endpoint === 'summary')
      return route.fulfill({ json: { ...report, summary: rows[0].summary } })
    if (endpoint === 'timeseries')
      return route.fulfill({
        contentType: 'application/json',
        body: timeseriesPayload,
      })
    if (endpoint === 'realtime')
      return route.fulfill({ json: { visitors: 0, minutes: 5 } })
    return route.fulfill({ json: { ...report, totalRows: 0, rows: [] } })
  })
  await page.goto(`${base}/`)
  await page.locator('.line-chart__line').first().waitFor()
  await page.waitForFunction(() => !document.querySelector('.report-loading'))
  assert.equal(await page.locator('.line-chart__point').count(), 0)
  assert.equal(
    await page.locator('.chart-data tbody tr').count(),
    0,
    'Closed disclosure creates no table rows',
  )
  assert.ok(
    (await page.locator('.line-chart *').count()) < 100,
    'SVG DOM stays bounded',
  )
  const paths = await page
    .locator('.line-chart__line')
    .evaluateAll((lines) => lines.map((line) => line.getAttribute('d') ?? ''))
  assert.equal(paths.length, 2)
  const bounds = await page.locator('.chart-wrap').boundingBox()
  assert.ok(bounds)
  const budget = chartPointBudget(bounds.width, bounds.height)
  for (const path of paths) {
    assert.ok((path.match(/[ML]/g) ?? []).length <= budget)
    assert.doesNotMatch(path, /NaN|Infinity|undefined/)
    assert.match(path, /^M 8\.000 /)
    assert.match(path, /L 94\.000 /)
  }
  // Peak positions retain their actual time, even after most points are removed.
  const x = (8 + (86 * 12_345) / (size - 1)).toFixed(3)
  assert.ok(paths[0].includes(`L ${x} 6.000`))
  const values = page.locator('.chart-data')
  await values.locator('summary').click()
  await values.locator('tbody tr').first().waitFor()
  assert.equal(await values.locator('tbody tr').count(), 100)
  let releaseRefresh: (() => void) | undefined
  const refreshGate = new Promise<void>((resolve) => { releaseRefresh = resolve })
  const holdRefresh = async (route: Route) => {
    await refreshGate
    await route.fallback()
  }
  await page.route('**/mock-analytics/**', holdRefresh)
  try {
    await page.getByRole('button', { name: 'Refresh reports', exact: true }).click()
    await page.locator('.page-heading .report-loading').waitFor()
    assert.equal(await page.locator('.metric-card').count(), 5, 'Metrics stay visible during refresh')
    assert.deepEqual(
      await page.locator('.line-chart__line').evaluateAll((lines) => lines.map((line) => line.getAttribute('d') ?? '')),
      paths,
      'Previous chart stays visible during refresh',
    )
    assert.equal(await values.locator('tbody tr').count(), 100, 'Expanded chart values stay visible during refresh')
  } finally {
    releaseRefresh?.()
    await page.waitForFunction(() => !document.querySelector('.report-loading'))
    await page.unroute('**/mock-analytics/**', holdRefresh)
  }
  const firstTime = await values.locator('tbody tr td').first().innerText()
  await values.getByRole('button', { name: 'Next', exact: true }).click()
  assert.equal(await values.locator('tbody tr').count(), 100)
  assert.notEqual(
    await values.locator('tbody tr td').first().innerText(),
    firstTime,
  )
  await values.locator('summary').click()
  await page.waitForFunction(
    () => !document.querySelector('.chart-data tbody tr'),
  )
  await page.setViewportSize({ width: 390, height: 844 })
  await page.waitForFunction(
    (desktopCount) => {
      const path =
        document.querySelector('.line-chart__line')?.getAttribute('d') ?? ''
      return (path.match(/[ML]/g) ?? []).length < desktopCount
    },
    (paths[0].match(/[ML]/g) ?? []).length,
  )
  await page.screenshot({
    path: 'test-results/large-chart-mobile.png',
    fullPage: true,
  })
  await page.setViewportSize({ width: 1440, height: 1100 })
  await page.screenshot({
    path: 'test-results/large-chart-desktop.png',
    fullPage: true,
  })
  console.log(
    `Large SVG check: ${size.toLocaleString('en')} source rows, at most ${budget} vertices per series, no point markers, lazy 100-row exact values, responsive sampling.`,
  )
  timeseriesPayload = JSON.stringify({
    ...report,
    totalRows: 1,
    rows: rows.slice(0, 1),
  })
  await page.goto(`${base}/`)
  await page.locator('.line-chart__point').first().waitFor()
  assert.equal(
    await page.locator('.line-chart__point').count(),
    2,
    'Single-point series remain visible with bounded markers',
  )
}
