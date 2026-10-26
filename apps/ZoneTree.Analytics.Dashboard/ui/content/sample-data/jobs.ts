import {
  type ComputedRef,
  computed,
  onMounted,
  onUnmounted,
  type SRef,
  sref,
} from 'regor'
import type { DashboardModel } from '../lib/dashboard-model'

export interface SampleBenchmarkResult {
  events: number
  days: number
  workers: number
  percentiles: boolean
  indexes: boolean
  geolocation: boolean
  preparationMs: number
  submissionMs: number
  ingestionMs: number
  eventsPerSecond: number
  ingested: number
  errors: number
  dropped: number
  rejected: number
  build: 'Debug' | 'Release'
  garbageCollection: string
  tieredCompilation: string
  fromDate: string
  toDate: string
}

export interface SampleBenchmarkStatus {
  state:
    | 'idle'
    | 'preparing'
    | 'running'
    | 'ingesting'
    | 'flushing'
    | 'compacting'
    | 'publishing'
    | 'cancelling'
    | 'completed'
    | 'cancelled'
    | 'failed'
  requested: number
  prepared: number
  submitted: number
  workers: number
  days: number
  percentiles: boolean
  indexes: boolean
  geolocation: boolean
  geolocationAvailable: boolean
  error: string | null
  result: SampleBenchmarkResult | null
  ingested: number
  elapsedMs: number
  eventsPerSecond: number
}

export interface SampleDataStatus {
  state:
    | 'idle'
    | 'generating'
    | 'compacting'
    | 'deleting'
    | 'completed'
    | 'cancelled'
    | 'failed'
  requested: number
  processed: number
  totalGenerated: number
  elapsedMs: number
  workers: number
  ingested: number
  eventsPerSecond: number
  averageEventsPerSecond: number
  error: string | null
  activeOperation:
    | 'generation'
    | 'benchmark'
    | 'deletion'
    | 'benchmark-deletion'
    | null
  benchmark: SampleBenchmarkStatus
}

export interface SampleDataJobs {
  status: SRef<SampleDataStatus | null>
  error: SRef<string>
  pending: SRef<boolean>
  busy: ComputedRef<boolean>
  perform: (action: string, body?: object) => Promise<void>
}

export function createSampleDataJobs(
  dashboard: DashboardModel,
): SampleDataJobs {
  const status = sref<SampleDataStatus | null>(null)
  const error = sref('')
  const pending = sref(false)
  const busy = computed(() => pending() || Boolean(status()?.activeOperation))
  const controller = new AbortController()
  let timer: ReturnType<typeof setTimeout> | undefined
  let pollingError = ''
  const request = async (
    action: string,
    body?: object,
  ): Promise<SampleDataStatus> => {
    const capability = dashboard.config().sampleData
    if (!capability)
      throw new Error('Sample data controls are not available on this host.')
    const response = await fetch(`${capability.managementUrl}/${action}`, {
      method: body ? 'POST' : 'GET',
      cache: 'no-store',
      credentials: 'same-origin',
      signal: AbortSignal.any([controller.signal, AbortSignal.timeout(10_000)]),
      ...(body
        ? {
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
          }
        : {}),
    })
    const result = (await response.json()) as SampleDataStatus
    if (!response.ok)
      throw new Error(
        result.error || `Sample data request failed (${response.status}).`,
      )
    return result
  }
  const refresh = async () => {
    if (timer) clearTimeout(timer)
    try {
      status(await request('status'))
      if (error() === pollingError) error('')
      pollingError = ''
    } catch (failure) {
      if (!controller.signal.aborted) {
        pollingError = (failure as Error).message
        error(pollingError)
      }
    } finally {
      if (!controller.signal.aborted && (busy() || !status()))
        timer = setTimeout(
          () => {
            void refresh()
          },
          status()?.activeOperation === 'benchmark' ? 250 : 1000,
        )
    }
  }
  onMounted(() => {
    if (dashboard.config().mode === 'live' && dashboard.config().sampleData)
      void refresh()
  })
  onUnmounted(() => {
    controller.abort()
    if (timer) clearTimeout(timer)
  })
  return {
    status,
    error,
    pending,
    busy,
    perform: async (action, body = {}) => {
      pending(true)
      error('')
      try {
        status(await request(action, body))
      } catch (failure) {
        if (!controller.signal.aborted) error((failure as Error).message)
      } finally {
        pending(false)
      }
      if (!controller.signal.aborted) await refresh()
    },
  }
}
