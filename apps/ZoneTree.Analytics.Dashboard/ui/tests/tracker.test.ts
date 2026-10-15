import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import { test } from 'vitest'

const script = readFileSync(
  new URL(
    '../../../../src/ZoneTree.Analytics.AspNetCore/tracker.js',
    import.meta.url,
  ),
  'utf8',
)

type Listener = () => void
type Listeners = Map<string, Listener[]>
type TrackerCall = [
  name: string,
  properties?: Record<string, string>,
  metrics?: Record<string, number>,
]
type Tracker = ((...args: TrackerCall) => void) & { q?: TrackerCall[] }
type HistoryMethod = (
  this: MockHistory,
  data: unknown,
  unused: string,
  url?: string | URL | null,
) => string
type MockHistory = Record<'pushState' | 'replaceState', HistoryMethod>
interface Payload {
  n: string
  u: string
  r: string | null
  h?: boolean
  p?: Record<string, string>
  m?: Record<string, number>
}
interface FetchOptions {
  method: string
  keepalive: boolean
  headers: Record<string, string>
  body: string
}
interface BrowserOptions {
  attrs?: Record<string, string>
  beacon?: boolean
  webdriver?: boolean
  url?: string
  prerender?: boolean
}
class MockBlob {
  readonly body: string
  readonly type: string

  constructor(parts: string[], options: { type: string }) {
    this.body = parts.join('')
    this.type = options.type
  }
}

function browser({
  attrs = {},
  beacon = true,
  webdriver = false,
  url = 'https://example.com/start',
  prerender = false,
}: BrowserOptions = {}) {
  const beacons: { endpoint: string; payload: Payload; type: string }[] = []
  const fetches: (FetchOptions & { endpoint: string; payload: Payload })[] = []
  const historyCalls: {
    method: keyof MockHistory
    args: Parameters<HistoryMethod>
  }[] = []
  const listeners: Listeners = new Map()
  const documentListeners: Listeners = new Map()
  const parsed = new URL(url)
  const location = { href: parsed.href, hostname: parsed.hostname }
  function historyMethod(method: keyof MockHistory): HistoryMethod {
    return function (this: MockHistory, ...args: Parameters<HistoryMethod>) {
      assert.equal(this, history)
      historyCalls.push({ method, args })
      if (args[2] != null) location.href = new URL(args[2], location.href).href
      return `${method}-result`
    }
  }
  const history: MockHistory = {
    pushState: historyMethod('pushState'),
    replaceState: historyMethod('replaceState'),
  }
  const addListener = (map: Listeners) => (name: string, handler: Listener) => {
    const handlers = map.get(name) || []
    handlers.push(handler)
    map.set(name, handlers)
  }
  const document = {
    currentScript: {
      src: 'https://example.com/_analytics/script.js',
      getAttribute: (name: string) => attrs[name] || null,
    },
    referrer: 'https://referrer.example/',
    visibilityState: prerender ? 'prerender' : 'visible',
    addEventListener: addListener(documentListeners),
    removeEventListener: (name: string, handler: Listener) =>
      documentListeners.set(
        name,
        (documentListeners.get(name) ?? []).filter((x) => x !== handler),
      ),
  }
  interface Sandbox {
    history: MockHistory
    location: typeof location
    document: typeof document
    navigator: {
      webdriver: boolean
      sendBeacon?: (endpoint: string, blob: MockBlob) => boolean
    }
    Blob: typeof MockBlob
    fetch: (endpoint: string, options: FetchOptions) => Promise<void>
    addEventListener: (name: string, handler: Listener) => void
    window?: Sandbox
    zta?: Tracker
  }
  const sandbox: Sandbox = {
    history,
    location,
    document,
    navigator: {
      webdriver,
      sendBeacon: (endpoint, blob) => {
        beacons.push({
          endpoint,
          payload: JSON.parse(blob.body),
          type: blob.type,
        })
        return beacon
      },
    },
    Blob: MockBlob,
    fetch: (endpoint, options) => {
      fetches.push({ endpoint, ...options, payload: JSON.parse(options.body) })
      return Promise.resolve()
    },
    addEventListener: addListener(listeners),
  }
  sandbox.window = sandbox
  const context = vm.createContext(sandbox)
  return {
    window: sandbox,
    document,
    beacons,
    fetches,
    historyCalls,
    listeners,
    install: () => vm.runInContext(script, context),
    dispatch: (name: string) =>
      (listeners.get(name) || []).forEach((handler) => {
        handler()
      }),
    show: () => {
      document.visibilityState = 'visible'
      ;(documentListeners.get('visibilitychange') || [])
        .slice()
        .forEach((handler) => {
          handler()
        })
    },
  }
}

test('both history methods preserve arguments, receiver and return value, and deduplicate URLs', () => {
  const b = browser()
  b.install()
  const state = { route: 1 }
  assert.equal(
    b.window.history.replaceState(state, '', '/replaced'),
    'replaceState-result',
  )
  assert.deepEqual(b.historyCalls[0], {
    method: 'replaceState',
    args: [state, '', '/replaced'],
  })
  assert.equal(
    b.window.history.pushState(null, '', '/pushed'),
    'pushState-result',
  )
  b.window.history.replaceState(null, '', '/pushed#anchor')
  b.dispatch('popstate')
  assert.deepEqual(
    b.beacons.map((x) => x.payload.u),
    [
      'https://example.com/start',
      'https://example.com/replaced',
      'https://example.com/pushed',
    ],
  )
  b.window.location.href = 'https://example.com/start'
  b.dispatch('popstate')
  assert.equal(b.beacons.length, 4)
})

test('duplicate installation retains a single tracker, history wrapper, and event subscription', () => {
  const b = browser()
  const queued: Tracker = () => {}
  queued.q = [['signup', { plan: 'pro' }, { value: 49 }]]
  b.window.zta = queued
  b.install()
  const tracker = b.window.zta,
    push = b.window.history.pushState,
    replace = b.window.history.replaceState
  b.install()
  assert.equal(b.window.zta, tracker)
  assert.equal(b.window.history.pushState, push)
  assert.equal(b.window.history.replaceState, replace)
  assert.equal(b.listeners.get('popstate')?.length, 1)
  assert.deepEqual(
    b.beacons.map((x) => x.payload.n),
    ['signup', 'pageview'],
  )
  assert.deepEqual(b.beacons[0].payload.p, { plan: 'pro' })
  assert.deepEqual(b.beacons[0].payload.m, { value: 49 })
  b.window.history.replaceState(null, '', '/next')
  assert.equal(b.beacons.length, 3)
})

test('failed or absent sendBeacon uses fetch with the same payload and configured endpoint', () => {
  for (const absent of [false, true]) {
    const b = browser({
      beacon: false,
      attrs: { 'data-endpoint': '/custom/event' },
    })
    if (absent) delete b.window.navigator.sendBeacon
    b.install()
    assert.equal(b.fetches.length, 1)
    assert.equal(b.fetches[0].endpoint, '/custom/event')
    assert.equal(b.fetches[0].method, 'POST')
    assert.equal(b.fetches[0].keepalive, true)
    assert.equal(b.fetches[0].headers['Content-Type'], 'text/plain')
    assert.equal(b.fetches[0].payload.n, 'pageview')
    assert.equal(b.fetches[0].payload.r, 'https://referrer.example/')
    if (!absent) assert.deepEqual(b.fetches[0].payload, b.beacons[0].payload)
  }
})

test('hash routing tracks replacement routes once even when hashchange fires too', () => {
  const b = browser({
    attrs: { 'data-hash-routing': 'true' },
    url: 'https://example.com/#/start',
  })
  b.install()
  b.window.history.replaceState(null, '', '#/next?utm_source=newsletter')
  b.dispatch('hashchange')
  b.window.history.pushState(null, '', '#!/last')
  assert.deepEqual(
    b.beacons.map((x) => x.payload.u),
    [
      'https://example.com/#/start',
      'https://example.com/#/next?utm_source=newsletter',
      'https://example.com/#!/last',
    ],
  )
  assert.ok(b.beacons.every((x) => x.payload.h === true))
})

test('history errors propagate without emitting a page view', () => {
  const b = browser()
  b.window.history.replaceState = () => {
    throw new Error('history failed')
  }
  b.install()
  assert.throws(
    () => b.window.history.replaceState(null, '', '/next'),
    /history failed/,
  )
  assert.equal(b.beacons.length, 1)
})

test('prerendered documents wait for visibility and remain installed only once', () => {
  const b = browser({ prerender: true })
  b.install()
  b.install()
  assert.equal(b.beacons.length, 0)
  b.show()
  b.show()
  assert.equal(b.beacons.length, 1)
})

test('automation and localhost stay excluded unless localhost tracking is explicitly enabled', () => {
  for (const options of [
    { webdriver: true },
    { url: 'http://localhost/start' },
  ]) {
    const b = browser(options)
    b.install()
    b.window.history.replaceState(null, '', '/next')
    assert.equal(b.beacons.length, 0)
    assert.equal(b.fetches.length, 0)
  }
  const b = browser({
    url: 'http://localhost/start',
    attrs: { 'data-allow-localhost': 'true' },
  })
  b.install()
  assert.equal(b.beacons.length, 1)
})
