import assert from 'node:assert/strict'
import { test, vi } from 'vitest'
import {
  ApiClient,
  normalizeApiUrl,
  parameters,
  postScope,
} from '../content/lib/api'
import { validateConfig } from '../content/lib/config'
import {
  type Breakdown,
  defaultConfig,
  type Page,
  type Point,
  type Report,
  type Scope,
  type SummaryReport,
} from '../content/lib/contracts'
import { DemoClient, demoDates } from '../content/lib/demo'
import {
  change,
  csv,
  duration,
  filterExpression,
  percent,
} from '../content/lib/format'

const scope: Scope = {
  site: 'store',
  range: '30d',
  compare: 'previous-period',
  tz: 'Europe/Berlin',
  filters: ['country==DE', 'device==Mobile'],
}
test('HTTP scope preserves repeated filters and inclusive custom dates; funnels omit comparisons', () => {
  const params = parameters(
    { ...scope, range: 'custom', from: '2026-09-01', to: '2026-09-30' },
    { limit: '20', offset: '40' },
  )
  assert.deepEqual(params.getAll('filter'), scope.filters)
  assert.equal(params.get('from'), '2026-09-01')
  assert.equal(params.get('to'), '2026-09-30')
  assert.equal(params.has('range'), false)
  assert.equal(params.get('offset'), '40')
  assert.equal('compare' in postScope(scope, true), false)
  assert.throws(() =>
    parameters({
      ...scope,
      range: 'custom',
      from: '2026-10-02',
      to: '2026-10-01',
    }),
  )
})
test('configuration validates endpoints, sites, timezone and refresh intervals', () => {
  assert.equal(normalizeApiUrl('/_analytics/api/'), '/_analytics/api')
  assert.equal(
    normalizeApiUrl('https://example.org/reports/'),
    'https://example.org/reports',
  )
  for (const invalid of [
    'javascript:alert(1)',
    '//example.org/api',
    'https://user:secret@example.org/api',
    '/api?token=secret',
  ])
    assert.throws(() => normalizeApiUrl(invalid))
  assert.throws(() =>
    validateConfig({
      ...defaultConfig,
      sites: [
        { id: 'a', name: 'A' },
        { id: 'a', name: 'B' },
      ],
    }),
  )
  assert.throws(() =>
    validateConfig({ ...defaultConfig, timeZone: 'Invalid/Zone' }),
  )
  assert.throws(() => validateConfig({ ...defaultConfig, refreshSeconds: 1 }))
})
test('null and zero stay distinct; CSV protects tracked strings against spreadsheet formulas', () => {
  assert.equal(percent(null), '—')
  assert.equal(percent(0), '0%')
  assert.equal(change(42, 0), null)
  assert.equal(change(120, 100), 0.2)
  assert.equal(duration(119.9), '2m 0s')
  assert.match(
    csv([['=SUM(A1)', '@command', '-path', 'quote"value', 0, null]]),
    /"'=SUM\(A1\)"/,
  )
  assert.match(csv([['quote"value']]), /"quote""value"/)
  assert.equal(filterExpression('page', '~=', '/blog'), 'page~=/blog')
  assert.throws(() => filterExpression('page', '==', '/a,/b'))
})
test('demo summary, pages, devices and time series share one dataset and honor filters', async () => {
  const demo = new DemoClient()
  const signal = new AbortController().signal
  const sample = { ...scope, range: '7d', filters: [], compare: 'none' }
  const summary = await demo.request<SummaryReport>('summary', sample, signal)
  const pages = await demo.request<Report<Page>>('pages', sample, signal, {
    limit: '100',
  })
  const devices = await demo.request<Report<Breakdown>>(
    'breakdown',
    sample,
    signal,
    {
      dimension: 'device',
      limit: '100',
    },
  )
  const points = await demo.request<Report<Point>>('timeseries', sample, signal)
  assert.equal(
    pages.rows.reduce((sum, row) => sum + row.pageViews, 0),
    summary.summary.pageViews,
  )
  assert.equal(
    devices.rows.reduce((sum, row) => sum + row.pageViews, 0),
    summary.summary.pageViews,
  )
  assert.equal(
    points.rows.reduce((sum, row) => sum + row.summary.pageViews, 0),
    summary.summary.pageViews,
  )
  assert.equal(points.rows.length, 7)
  const focused = await demo.request<SummaryReport>(
    'summary',
    { ...sample, filters: ['country==DE'] },
    signal,
  )
  assert.ok(focused.summary.visitors < summary.summary.visitors)
  const pageFiltered = await demo.request<SummaryReport>(
    'summary',
    { ...sample, filters: ['page==/pricing'] },
    signal,
  )
  assert.equal(pageFiltered.summary.sessions, null)
  assert.equal(pageFiltered.summary.bounceRate, null)
  const [from, to] = demoDates(
    { ...sample, range: 'last-month' },
    new Date('2026-10-12T08:00:00Z'),
  )
  assert.equal(from.toISOString(), '2026-09-01T00:00:00.000Z')
  assert.equal(to.toISOString(), '2026-10-01T00:00:00.000Z')
})
test('overview metric options preserve demo counts and comparisons', async () => {
  const demo = new DemoClient()
  const signal = new AbortController().signal
  for (const dimension of ['source', 'country', 'device']) {
    const full = await demo.request<Report<Breakdown>>(
      'breakdown',
      scope,
      signal,
      {
        dimension,
      },
    )
    const projected = await demo.request<Report<Breakdown>>(
      'breakdown',
      scope,
      signal,
      {
        dimension,
        includeSessionMetrics: 'false',
      },
    )
    const strip = (row: Breakdown): Breakdown => ({
      ...row,
      sessions: null,
      bounceRate: null,
      averageSessionDuration: null,
      ...(row.previous ? { previous: strip(row.previous) } : {}),
    })
    assert.deepEqual(projected.rows, full.rows.map(strip))
    assert.equal(projected.totalRows, full.totalRows)
  }
  const full = await demo.request<Report<Page>>('pages', scope, signal)
  const projected = await demo.request<Report<Page>>('pages', scope, signal, {
    includePageFlowMetrics: 'false',
  })
  const strip = (row: Page): Page => ({
    ...row,
    entrances: null,
    exits: null,
    exitRate: null,
    ...(row.previous ? { previous: strip(row.previous) } : {}),
  })
  assert.deepEqual(projected.rows, full.rows.map(strip))
  assert.equal(projected.totalRows, full.totalRows)
  await assert.rejects(
    demo.request('exit-pages', scope, signal, {
      includePageFlowMetrics: 'false',
    }),
    /page-flow/,
  )
})

test('API client reserves at most three slots and removes aborted queued requests', async () => {
  const originalFetch = globalThis.fetch
  const pending: (() => void)[] = []
  let active = 0,
    maximum = 0,
    calls = 0
  globalThis.fetch = async () => {
    active++
    calls++
    maximum = Math.max(maximum, active)
    await new Promise<void>((resolve) => pending.push(resolve))
    active--
    return new Response('{"ok":true}', {
      headers: { 'Content-Type': 'application/json' },
    })
  }
  try {
    const client = new ApiClient(defaultConfig)
    const controller = new AbortController()
    const abort = new AbortController()
    const requests = [0, 1, 2].map(() =>
      client.request('summary', scope, controller.signal),
    )
    const aborted = client
      .request('summary', scope, abort.signal)
      .catch((error) => error)
    const queued = client.request('summary', scope, controller.signal)
    await new Promise((resolve) => setImmediate(resolve))
    assert.equal(calls, 3)
    abort.abort()
    assert.equal(((await aborted) as Error).name, 'AbortError')
    const complete = pending.shift()
    assert.ok(complete, 'An active request must be pending')
    complete()
    await new Promise((resolve) => setImmediate(resolve))
    assert.equal(calls, 4)
    assert.equal(maximum, 3)
    for (const resolve of pending.splice(0)) resolve()
    await Promise.all([...requests, queued])
  } finally {
    globalThis.fetch = originalFetch
  }
})
test('API errors remain explicit and HTML sign-in responses are rejected', async () => {
  const originalFetch = globalThis.fetch
  const client = new ApiClient(defaultConfig)
  const signal = new AbortController().signal
  try {
    globalThis.fetch = async () =>
      new Response('{"error":"Access denied"}', {
        status: 403,
        headers: { 'Content-Type': 'application/json' },
      })
    await assert.rejects(
      client.request('summary', scope, signal),
      /Access denied/,
    )
    globalThis.fetch = async () =>
      new Response('<html>Sign in</html>', {
        headers: { 'Content-Type': 'text/html' },
      })
    await assert.rejects(
      client.request('summary', scope, signal),
      /HTML instead of JSON/,
    )
  } finally {
    globalThis.fetch = originalFetch
  }
})

test('API timing includes response decoding and ignores failed or cancelled responses', async () => {
  let clock = 100
  const timings: [string, number][] = []
  vi.spyOn(performance, 'now').mockImplementation(() => clock)
  const client = new ApiClient(defaultConfig, (endpoint, milliseconds) =>
    timings.push([endpoint, milliseconds]),
  )
  const controller = new AbortController()
  vi.spyOn(globalThis, 'fetch').mockImplementation(async () => {
    clock += 12
    const response = new Response('{"ok":true}', {
      headers: { 'Content-Type': 'application/json' },
    })
    response.json = async () => {
      clock += 5
      return { ok: true }
    }
    return response
  })
  assert.deepEqual(await client.request('summary', scope, controller.signal), {
    ok: true,
  })
  assert.deepEqual(timings, [['summary', 17]])
  vi.spyOn(globalThis, 'fetch').mockImplementation(
    async () =>
      new Response('{"error":"Failed"}', {
        status: 503,
        headers: { 'Content-Type': 'application/json' },
      }),
  )
  await assert.rejects(
    client.request('summary', scope, controller.signal),
    /Failed/,
  )
  vi.spyOn(globalThis, 'fetch').mockImplementation(async () => {
    const response = new Response('{"ok":true}', {
      headers: { 'Content-Type': 'application/json' },
    })
    response.json = async () => {
      controller.abort()
      return { ok: true }
    }
    return response
  })
  await assert.rejects(client.request('summary', scope, controller.signal), {
    name: 'AbortError',
  })
  assert.deepEqual(timings, [['summary', 17]])
})

test('performance sample reports use the isolated endpoint while other sites use the primary API', async () => {
  const urls: string[] = []
  vi.spyOn(globalThis, 'fetch').mockImplementation(async (url) => {
    urls.push(String(url))
    return new Response('{}', {
      headers: { 'Content-Type': 'application/json' },
    })
  })
  const client = new ApiClient({
    ...defaultConfig,
    mode: 'live',
    sampleData: {
      siteId: 'performance-samples',
      apiBaseUrl: '/_analytics/sample-data/api',
      managementUrl: '/_analytics/sample-data',
      benchmark: {
        siteId: 'benchmark-results',
        apiBaseUrl: '/_analytics/sample-data/benchmark-api',
      },
    },
  })
  await client.request(
    'summary',
    { ...scope, site: 'performance-samples' },
    new AbortController().signal,
  )
  await client.request('summary', scope, new AbortController().signal)
  await client.request(
    'summary',
    { ...scope, site: 'benchmark-results' },
    new AbortController().signal,
  )
  assert.ok(urls[0].startsWith('/_analytics/sample-data/api/summary?'))
  assert.ok(urls[1].startsWith('/_analytics/api/summary?'))
  assert.ok(
    urls[2].startsWith('/_analytics/sample-data/benchmark-api/summary?'),
  )
})
