import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import type { Page } from 'playwright'
import { dashboardConfigPath } from '../content/lib/contracts'
import type {
  SampleBenchmarkResult,
  SampleDataStatus,
} from '../content/sample-data/jobs'

// UI checks use controlled responses; performance runs are left to the user.
export async function checkSampleDataTools(page: Page, base: string) {
  const result: SampleBenchmarkResult = {
    events: 17,
    days: 7,
    workers: 2,
    percentiles: false,
    indexes: true,
    preparationMs: 100,
    submissionMs: 200,
    ingestionMs: 500,
    eventsPerSecond: 34,
    ingested: 17,
    errors: 0,
    dropped: 0,
    rejected: 0,
    build: 'Release',
    garbageCollection: 'Server',
    tieredCompilation: 'Disabled',
    fromDate: '2026-10-06',
    toDate: '2026-10-13',
  }
  let status: SampleDataStatus = {
    state: 'completed',
    requested: 17,
    processed: 17,
    totalGenerated: 17,
    elapsedMs: 1000,
    workers: 1,
    ingested: 17,
    eventsPerSecond: 17,
    averageEventsPerSecond: 17,
    error: null,
    activeOperation: null,
    benchmark: {
      state: 'idle',
      requested: 0,
      prepared: 0,
      submitted: 0,
      workers: 1,
      days: 30,
      percentiles: false,
      indexes: true,
      error: null,
      result,
      ingested: 0,
      elapsedMs: 0,
      eventsPerSecond: 0,
    },
  }
  const submissions: object[] = []
  await page.unroute(`**${dashboardConfigPath}`)
  await page.route(`**${dashboardConfigPath}`, (route) =>
    route.fulfill({
      json: {
        mode: 'live',
        apiBaseUrl: '/mock-analytics',
        timeZone: 'UTC',
        sites: [{ id: '', name: 'Test site' }],
        refreshSeconds: 0,
        sampleData: {
          siteId: 'performance-samples',
          apiBaseUrl: '/mock-samples/api',
          managementUrl: '/mock-samples',
          benchmark: {
            siteId: 'benchmark-results',
            apiBaseUrl: '/mock-benchmark/api',
          },
        },
      },
    }),
  )
  await page.route('**/mock-samples/**', async (route) => {
    const endpoint = new URL(route.request().url()).pathname.split('/').pop()
    if (endpoint === 'benchmark') {
      submissions.push(route.request().postDataJSON())
      status = {
        ...status,
        activeOperation: 'benchmark',
        benchmark: {
          ...status.benchmark,
          state: 'preparing',
          requested: 17,
          prepared: 0,
          submitted: 0,
          workers: 2,
          days: 7,
          percentiles: true,
          indexes: false,
        },
      }
    } else if (endpoint === 'cancel') {
      status = {
        ...status,
        activeOperation: null,
        benchmark: { ...status.benchmark, state: 'cancelled' },
      }
    }
    await route.fulfill({ json: status })
  })
  await page.goto(`${base}/sample-data/`)
  await page
    .getByRole('button', { name: 'Run benchmark', exact: true })
    .waitFor()
  await page.waitForFunction(
    () =>
      !document
        .querySelector('.sample-benchmark-panel button[type="submit"]')
        ?.hasAttribute('disabled'),
  )
  await page.getByText('Generation throughput', { exact: true }).waitFor()
  await page
    .getByRole('region', { name: 'Last completed benchmark', exact: true })
    .waitFor()
  assert.equal(
    await page
      .getByLabel('Include load-time percentile aggregation')
      .isChecked(),
    false,
  )
  const benchmarkPanel = page.locator('.sample-benchmark-panel')
  await benchmarkPanel.getByLabel('Events').fill('17')
  await benchmarkPanel.getByLabel('Days').fill('7')
  await benchmarkPanel.getByLabel('Workers').fill('2')
  await page
    .getByText('Include load-time percentile aggregation', { exact: true })
    .click()
  assert.equal(
    await page.getByLabel('Maintain dimension and rollup indexes').isChecked(),
    true,
  )
  await page
    .getByText('Maintain dimension and rollup indexes', { exact: true })
    .click()
  assert.equal(
    await page
      .getByLabel('Include load-time percentile aggregation')
      .isChecked(),
    true,
  )
  await page.getByRole('button', { name: 'Run benchmark', exact: true }).click()
  await page.getByText('Preparing 0 / 17 events', { exact: true }).waitFor()
  assert.deepEqual(submissions, [
    { events: 17, days: 7, workers: 2, percentiles: true, indexes: false },
  ])
  assert.equal(
    await page
      .getByRole('button', { name: 'Generate sample data', exact: true })
      .isDisabled(),
    true,
  )
  assert.equal(
    await page
      .getByRole('button', { name: 'Delete sample data', exact: true })
      .isDisabled(),
    true,
  )
  assert.equal(
    await page
      .getByRole('region', { name: 'Last completed benchmark', exact: true })
      .count(),
    1,
    'Previous benchmark remains visible',
  )
  status = {
    ...status,
    benchmark: {
      ...status.benchmark,
      state: 'ingesting',
      prepared: 17,
      submitted: 17,
      ingested: 10,
      elapsedMs: 500,
      eventsPerSecond: 20,
    },
  }
  await page
    .getByText('10 / 17 ingested · 17 submitted · 0.500 s', { exact: true })
    .waitFor()
  assert.equal(
    await page.getByLabel('Benchmark ingestion progress').getAttribute('value'),
    '10',
  )
  status = {
    ...status,
    benchmark: {
      ...status.benchmark,
      state: 'flushing',
      submitted: 17,
      ingested: 17,
    },
  }
  await page.getByText('Saving aggregates…', { exact: true }).waitFor()
  status = {
    ...status,
    activeOperation: null,
    benchmark: {
      ...status.benchmark,
      state: 'completed',
      result: { ...result, percentiles: true, indexes: false },
    },
  }
  await page.getByText('Benchmark completed', { exact: true }).waitFor()
  assert.equal(
    await page
      .getByRole('link', { name: 'View benchmark reports', exact: true })
      .getAttribute('href'),
    '/?site=benchmark-results&range=custom&from=2026-10-06&to=2026-10-13&compare=none',
  )
  assert.equal(
    await page
      .getByRole('button', { name: 'Generate sample data', exact: true })
      .isEnabled(),
    true,
  )
  await page
    .getByText(
      '17 events · 7 days · 2 workers · Percentiles on · Indexes off',
      {
        exact: true,
      },
    )
    .waitFor()
  assert.equal(
    await page
      .getByText('17 generated events in this dataset', { exact: true })
      .count(),
    1,
  )
  await page.getByRole('button', { name: 'Run benchmark', exact: true }).click()
  await page
    .getByRole('button', { name: 'Cancel benchmark', exact: true })
    .click()
  await page.getByText('Benchmark cancelled', { exact: true }).waitFor()
  assert.equal(
    await page
      .getByRole('region', { name: 'Last completed benchmark', exact: true })
      .count(),
    1,
  )
  await page.setViewportSize({ width: 390, height: 844 })
  assert.ok(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
    'Sample tools fit mobile viewport',
  )
  await page.screenshot({
    path: fileURLToPath(
      new URL('../test-results/sample-tools-mobile.png', import.meta.url),
    ),
    fullPage: true,
  })
  await page.setViewportSize({ width: 1440, height: 1100 })
  await page.screenshot({
    path: fileURLToPath(
      new URL('../test-results/sample-tools-desktop.png', import.meta.url),
    ),
    fullPage: true,
  })
  console.log(
    'Sample tools checks passed: benchmark inputs, percentile and index toggles, stages, shared busy state, cancellation, previous results and mobile layout (mock responses).',
  )
}
