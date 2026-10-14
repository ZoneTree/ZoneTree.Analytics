import { ApiClient } from '../api'
import { readStorage } from '../config'
import type { Plan } from '../contracts'
import { DemoClient } from '../demo'
import type { DashboardScope } from './scope'
import type { DashboardStatus } from './state'

export interface DashboardRequests {
  api: ApiClient
  request<T>(
    endpoint: string,
    signal: AbortSignal,
    extra?: Record<string, string>,
    body?: Record<string, unknown>,
  ): Promise<T>
}

export function createDashboardRequests(
  scope: DashboardScope,
  status: DashboardStatus,
): DashboardRequests {
  const config = scope.config()
  const api = new ApiClient(config, (endpoint, milliseconds) => {
    if (endpoint !== 'realtime' && scope.config().mode === 'live')
      status.apiResponseMs(Math.round(milliseconds))
  })
  const storedToken = readStorage('zt-dashboard-token-v1', true) as {
    base?: string
    token?: string
  } | null
  api.token =
    storedToken?.base === config.apiBaseUrl &&
    typeof storedToken.token === 'string'
      ? storedToken.token
      : ''
  const demo = new DemoClient()
  return {
    api,
    request: async <T>(
      endpoint: string,
      signal: AbortSignal,
      extra: Record<string, string> = {},
      body?: Record<string, unknown>,
    ): Promise<T> => {
      const source = scope.config().mode === 'live' ? api : demo
      const result = await source.request<T>(
        endpoint,
        scope.scope(),
        signal,
        extra,
        body,
      )
      signal.throwIfAborted()
      const plan = (result as { plan?: Plan }).plan
      if (plan) {
        const messages = [
          ...plan.warnings,
          ...(!plan.complete
            ? [
                'This report is incomplete. Review the reporting range and raw-event retention.',
              ]
            : []),
        ]
        status.warnings([...new Set([...status.warnings(), ...messages])])
      }
      return result
    },
  }
}
