import { batch, observe, type SRef } from 'regor'
import { parameters } from '../api'
import type { Config, Realtime } from '../contracts'
import type { DashboardModel, Loader } from '../dashboard-model'
import type { DashboardExport } from './exports'
import { watchLiveReports } from './live'
import type { DashboardRequests } from './requests'
import { type DashboardScope, persistDashboardScope } from './scope'
import type { DashboardReports, DashboardStatus } from './state'

export interface DashboardLifecycle {
  load: () => Promise<void>
  execute<T>(endpoint: string, body: Record<string, unknown>): Promise<T | null>
  start: (loader: Loader | null) => void
  updateConfig: (config: Config) => void
  registerCleanup: (stop: () => void) => void
  dispose: () => void
  applyDates: () => void
}

export interface DashboardRuntimeOptions {
  scope: DashboardScope
  reports: DashboardReports
  status: DashboardStatus
  exports: DashboardExport
  requests: DashboardRequests
  getModel: () => DashboardModel
}

export interface DashboardRuntime {
  lifecycle: DashboardLifecycle
  queueLoad: () => void
}

export function createDashboardRuntime(
  options: DashboardRuntimeOptions,
): DashboardRuntime {
  const { scope, reports, status, exports, requests, getModel } = options
  let controller: AbortController | null = null
  let loader: Loader | null = null
  let initialized = false
  let queued: ReturnType<typeof setTimeout> | null = null
  let refreshTimer: ReturnType<typeof setInterval> | null = null
  let stopLive: (() => void) | null = null
  let liveReady = false
  let liveRefreshPending = false
  const cleanup: (() => void)[] = []

  function beginRequest(): AbortSignal {
    controller?.abort()
    controller = new AbortController()
    batch(() => {
      status.loading(true)
      status.error('')
      status.warnings([])
      status.apiResponseMs(null)
      exports.exportRows([])
    })
    return controller.signal
  }

  function updateTime() {
    status.updatedAt(
      new Date().toLocaleTimeString('en', {
        hour: '2-digit',
        minute: '2-digit',
      }),
    )
  }

  function syncScope() {
    const value = scope.scope()
    parameters(value)
    persistDashboardScope(value)
  }

  async function updateRealtime(signal: AbortSignal) {
    try {
      const value = await requests.request<Realtime>('realtime', signal)
      if (!signal.aborted && !liveReady) reports.realtime(value.visitors)
    } catch {
      if (!signal.aborted && !liveReady) reports.realtime(null)
    }
  }

  async function load() {
    const signal = beginRequest()
    try {
      syncScope()
      if (scope.config().mode === 'demo')
        status.warnings([
          'Demo data is synthetic. Demo reporting dates use UTC; live reports use the configured time zone.',
        ])
      if (loader) {
        await Promise.all([
          loader(getModel(), signal),
          liveReady ? undefined : updateRealtime(signal),
        ])
        signal.throwIfAborted()
        updateTime()
      }
    } catch (failure) {
      if (!signal.aborted) {
        status.error(
          failure instanceof Error ? failure.message : String(failure),
        )
        exports.exportRows([])
      }
    } finally {
      if (!signal.aborted) {
        status.loading(false)
        if (liveRefreshPending) refreshFromLive()
      }
    }
  }

  async function execute<T>(
    endpoint: string,
    body: Record<string, unknown>,
  ): Promise<T | null> {
    const signal = beginRequest()
    try {
      syncScope()
      const result = await requests.request<T>(endpoint, signal, {}, body)
      signal.throwIfAborted()
      updateTime()
      return result
    } catch (failure) {
      if (!signal.aborted)
        status.error(
          failure instanceof Error ? failure.message : String(failure),
        )
      return null
    } finally {
      if (!signal.aborted) status.loading(false)
    }
  }

  function queueLoad() {
    if (!initialized) return
    if (queued) clearTimeout(queued)
    controller?.abort()
    status.loading(true)
    exports.exportRows([])
    queued = setTimeout(() => {
      queued = null
      void load()
    }, 0)
  }

  function stopPolling() {
    if (refreshTimer !== null) clearInterval(refreshTimer)
    refreshTimer = null
  }

  function startPolling() {
    const seconds = scope.config().refreshSeconds
    if (seconds && loader && refreshTimer === null && !document.hidden)
      refreshTimer = setInterval(() => {
        if (!document.hidden && !status.loading()) void load()
      }, seconds * 1000)
  }

  function refreshFromLive() {
    if (document.hidden) return
    liveRefreshPending = status.loading()
    if (!liveRefreshPending) void load()
  }

  function stopRefresh() {
    stopLive?.()
    stopLive = null
    liveReady = false
    liveRefreshPending = false
    stopPolling()
  }

  function setupRefresh(initialReportLoad = false) {
    stopRefresh()
    const config = scope.config()
    const site = scope.site()
    if (!loader || !config.refreshSeconds || document.hidden) return
    startPolling()
    if (
      config.mode !== 'live' ||
      !config.liveUrl ||
      site === config.benchmark?.siteId ||
      typeof WebSocket === 'undefined'
    )
      return
    let skipFirstSnapshot = initialReportLoad
    let awaitFollowUp = false
    stopLive = watchLiveReports(
      config.liveUrl,
      site,
      ({ visitors, reportsChanged }) => {
        liveReady = true
        stopPolling()
        reports.realtime(visitors)
        if (skipFirstSnapshot) {
          skipFirstSnapshot = false
          // The next sample also catches writes between the HTTP load and subscription.
          awaitFollowUp = true
          return
        }
        if (awaitFollowUp || reportsChanged) {
          awaitFollowUp = false
          refreshFromLive()
        }
      },
      () => {
        liveReady = false
        startPolling()
      },
    )
  }

  return {
    queueLoad,
    lifecycle: {
      load,
      execute,
      applyDates: queueLoad,
      start: (reportLoader) => {
        loader = reportLoader
        initialized = true
        for (const source of [
          scope.site,
          scope.range,
          scope.compare,
          scope.filters,
        ])
          cleanup.push(
            observe(source as SRef<unknown>, () => {
              if (source === scope.site) {
                reports.realtime(null)
                setupRefresh(true)
              }
              getModel().offset(0)
              queueLoad()
            }),
          )
        const visibilityChanged = () => {
          setupRefresh()
          if (!stopLive && loader && scope.config().refreshSeconds)
            refreshFromLive()
        }
        document.addEventListener('visibilitychange', visibilityChanged)
        cleanup.push(() =>
          document.removeEventListener('visibilitychange', visibilityChanged),
        )
        setupRefresh(true)
        if (loader) void load()
      },
      updateConfig: (config) => {
        status.apiResponseMs(null)
        scope.config(config)
        requests.api.config = config
        setupRefresh()
      },
      registerCleanup: (stop) => {
        cleanup.push(stop)
      },
      dispose: () => {
        controller?.abort()
        if (queued) clearTimeout(queued)
        stopRefresh()
        for (const stop of cleanup) stop()
      },
    },
  }
}
