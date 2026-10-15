import { batch, observe, type SRef } from 'regor'
import { parameters } from '../api'
import type { Config, Realtime } from '../contracts'
import type { DashboardModel, Loader } from '../dashboard-model'
import type { DashboardExport } from './exports'
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
      reports.realtime(
        (await requests.request<Realtime>('realtime', signal)).visitors,
      )
    } catch {
      if (!signal.aborted) reports.realtime(null)
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
        await Promise.all([loader(getModel(), signal), updateRealtime(signal)])
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
      if (!signal.aborted) status.loading(false)
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

  function setupRefresh() {
    if (refreshTimer) clearInterval(refreshTimer)
    const seconds = scope.config().refreshSeconds
    if (seconds && loader)
      refreshTimer = setInterval(() => {
        if (!document.hidden && !status.loading()) void load()
      }, seconds * 1000)
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
              getModel().offset(0)
              queueLoad()
            }),
          )
        setupRefresh()
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
        if (refreshTimer) clearInterval(refreshTimer)
        for (const stop of cleanup) stop()
      },
    },
  }
}
