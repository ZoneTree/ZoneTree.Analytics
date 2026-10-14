import assert from 'node:assert/strict'
import { type TestContext, test } from 'node:test'
import { setImmediate as nextTurn } from 'node:timers/promises'
import { loadConfig } from '../content/lib/config'
import { defaultConfig } from '../content/lib/contracts'
import { createDashboardModel } from '../content/lib/dashboard-model'

function memoryStorage(): Storage {
  const items = new Map<string, string>()
  return {
    get length() {
      return items.size
    },
    key: (index) => [...items.keys()][index] ?? null,
    getItem: (key) => items.get(key) ?? null,
    setItem: (key, value) => {
      items.set(key, value)
    },
    removeItem: (key) => {
      items.delete(key)
    },
    clear: () => {
      items.clear()
    },
  }
}

function browserEnvironment(context: TestContext, url = 'http://localhost/') {
  const location = new URL(url)
  const localStorage = memoryStorage()
  const sessionStorage = memoryStorage()
  const document = {
    hidden: false,
    body: { dataset: { basePath: '/' } },
    getElementById: () => null,
  }
  const history = {
    replaceState: (
      _data: unknown,
      _unused: string,
      value?: string | URL | null,
    ) => {
      if (value != null) location.href = new URL(String(value), location).href
    },
  }
  const globals = { location, localStorage, sessionStorage, document, history }
  const originals = new Map<string, PropertyDescriptor | undefined>()
  for (const [name, value] of Object.entries(globals)) {
    originals.set(name, Object.getOwnPropertyDescriptor(globalThis, name))
    Object.defineProperty(globalThis, name, { configurable: true, value })
  }
  context.after(() => {
    for (const [name, descriptor] of originals) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor)
      else Reflect.deleteProperty(globalThis, name)
    }
  })
  return globals
}

test('sample controls are advertised by the host rather than restored from browser settings', async (context) => {
  const browser = browserEnvironment(context)
  browser.localStorage.setItem(
    'zt-dashboard-connection-v1',
    JSON.stringify({
      ...defaultConfig,
      mode: 'live',
      sampleData: {
        siteId: 'stale-sample',
        apiBaseUrl: '/stale/api',
        managementUrl: '/stale',
      },
    }),
  )
  context.mock.method(
    globalThis,
    'fetch',
    async () =>
      new Response(JSON.stringify({ ...defaultConfig, mode: 'live' }), {
        headers: { 'Content-Type': 'application/json' },
      }),
  )
  assert.equal((await loadConfig()).sampleData, undefined)
  context.mock.method(
    globalThis,
    'fetch',
    async () =>
      new Response(
        JSON.stringify({
          ...defaultConfig,
          mode: 'live',
          sampleData: {
            siteId: 'performance-samples',
            apiBaseUrl: '/_analytics/sample-data/api',
            managementUrl: '/_analytics/sample-data',
            benchmark: { siteId: 'benchmark-results', apiBaseUrl: '/_analytics/sample-data/benchmark-api' },
          },
        }),
        { headers: { 'Content-Type': 'application/json' } },
      ),
  )
  const config = await loadConfig()
  assert.equal(config.sampleData?.siteId, 'performance-samples')
  assert.ok(config.sites.some((site) => site.id === 'performance-samples'))
  assert.ok(config.sites.some((site) => site.id === 'benchmark-results'))
})

test('URL scope overrides session settings and persists custom dates, repeated filters, and no comparison', async (context) => {
  const browser = browserEnvironment(
    context,
    'http://localhost/?site=url-site&range=custom&from=2026-10-01&to=2026-10-12&compare=none&filter=country==DE&filter=device==Mobile',
  )
  browser.sessionStorage.setItem(
    'zt-dashboard-scope-v1',
    JSON.stringify({
      site: 'stored-site',
      range: '7d',
      compare: 'previous-year',
      filters: ['source==Google'],
    }),
  )
  const model = createDashboardModel('overview', {
    ...defaultConfig,
    timeZone: 'UTC',
  })
  try {
    assert.deepEqual(model.scope(), {
      site: 'url-site',
      range: 'custom',
      compare: 'none',
      tz: 'UTC',
      from: '2026-10-01',
      to: '2026-10-12',
      filters: ['country==DE', 'device==Mobile'],
    })
    await model.load()
    assert.equal(browser.location.searchParams.get('compare'), 'none')
    assert.deepEqual(browser.location.searchParams.getAll('filter'), [
      'country==DE',
      'device==Mobile',
    ])
    assert.deepEqual(
      JSON.parse(
        browser.sessionStorage.getItem('zt-dashboard-scope-v1') ?? 'null',
      ),
      model.scope(),
    )
  } finally {
    model.dispose()
  }
})

test('changing scope aborts the previous load and disposal removes scope observers', {
  timeout: 2000,
}, async (context) => {
  browserEnvironment(context)
  const model = createDashboardModel('overview', {
    ...defaultConfig,
    refreshSeconds: 0,
  })
  const signals: AbortSignal[] = []
  const completions: (() => void)[] = []
  let onSecondLoad: (() => void) | undefined
  const secondLoad = new Promise<void>((resolve) => {
    onSecondLoad = resolve
  })
  try {
    model.start(async (_dashboard, signal) => {
      signals.push(signal)
      await new Promise<void>((resolve) => {
        completions.push(resolve)
        if (signals.length === 2) onSecondLoad?.()
      })
      signal.throwIfAborted()
    })
    assert.equal(signals.length, 1)
    model.site('another-site')
    assert.equal(signals[0].aborted, true)
    await secondLoad
    assert.equal(signals.length, 2)
    model.dispose()
    assert.equal(signals[1].aborted, true)
    for (const finish of completions) finish()
    model.site('after-disposal')
    await new Promise((resolve) => setTimeout(resolve, 10))
    assert.equal(signals.length, 2)
  } finally {
    model.dispose()
    for (const finish of completions) finish()
  }
})

test('query failures clear exports, recover with plan warnings, and reject invalid dates before requesting', async (context) => {
  browserEnvironment(context)
  let unavailable = true
  let calls = 0
  context.mock.method(globalThis, 'fetch', async () => {
    calls++
    return new Response(
      JSON.stringify(
        unavailable
          ? { error: 'Reporting is busy.' }
          : {
              rows: [],
              plan: {
                complete: false,
                warnings: ['Short retention', 'Short retention'],
              },
            },
      ),
      {
        status: unavailable ? 503 : 200,
        headers: { 'Content-Type': 'application/json' },
      },
    )
  })
  const model = createDashboardModel('explorer', {
    ...defaultConfig,
    mode: 'live',
  })
  try {
    model.exportRows([['Old results']])
    assert.equal(await model.execute('query', {}), null)
    assert.equal(model.error(), 'Reporting is busy.')
    assert.equal(model.loading(), false)
    assert.equal(model.exportRows().length, 0)
    unavailable = false
    assert.ok(await model.execute('query', {}))
    assert.equal(model.error(), '')
    assert.equal(
      model.warnings().filter((warning) => warning === 'Short retention')
        .length,
      1,
    )
    assert.ok(
      model.warnings().some((warning) => warning.includes('incomplete')),
    )
    assert.ok(model.updatedAt())
    model.range('custom')
    model.dateFrom('2026-10-12')
    model.dateTo('2026-10-01')
    assert.equal(await model.execute('query', {}), null)
    assert.equal(calls, 2)
    assert.match(model.error(), /valid start and end date/)
  } finally {
    model.dispose()
  }
})

test('refresh skips hidden documents, follows updated configuration, and stops on disposal', async (context) => {
  const browser = browserEnvironment(context)
  context.mock.timers.enable({ apis: ['setInterval'] })
  const model = createDashboardModel('overview', {
    ...defaultConfig,
    refreshSeconds: 30,
  })
  let loads = 0
  try {
    model.start(async () => {
      loads++
    })
    await nextTurn()
    browser.document.hidden = true
    context.mock.timers.tick(30_000)
    assert.equal(loads, 1)
    browser.document.hidden = false
    context.mock.timers.tick(30_000)
    await nextTurn()
    assert.equal(loads, 2)
    model.updateConfig({ ...model.config(), refreshSeconds: 60 })
    context.mock.timers.tick(30_000)
    assert.equal(loads, 2)
    context.mock.timers.tick(30_000)
    await nextTurn()
    assert.equal(loads, 3)
    model.dispose()
    context.mock.timers.tick(60_000)
    assert.equal(loads, 3)
  } finally {
    model.dispose()
  }
})
