import type { Config, Scope } from './contracts'
import { redirectToSignIn } from './session'

export function normalizeApiUrl(value: string): string {
  const base = value.trim().replace(/\/+$/, '')
  if (!base || base.startsWith('//'))
    throw new Error('Enter an API path or an http(s) URL.')
  const url = new URL(base, 'http://localhost')
  if (
    base.includes('\\') ||
    (!base.startsWith('/') && !/^https?:\/\//i.test(base))
  )
    throw new Error(
      'Use a rooted API path such as /_analytics/api or an absolute http(s) URL.',
    )
  if (
    !['http:', 'https:'].includes(url.protocol) ||
    url.username ||
    url.password ||
    url.search ||
    url.hash
  )
    throw new Error(
      'The API URL must use http(s), without credentials, a query, or a fragment.',
    )
  return base
}
export function parameters(
  scope: Scope,
  extra: Record<string, string> = {},
): URLSearchParams {
  const params = new URLSearchParams({
    site: scope.site,
    tz: scope.tz,
    ...extra,
  })
  if (scope.range === 'custom') {
    if (!scope.from || !scope.to || scope.from > scope.to)
      throw new Error('Choose a valid start and end date.')
    params.set('from', scope.from)
    params.set('to', scope.to)
  } else params.set('range', scope.range)
  if (scope.compare !== 'none') params.set('compare', scope.compare)
  for (const filter of scope.filters) params.append('filter', filter)
  return params
}
export function postScope(
  scope: Scope,
  funnel = false,
): Record<string, unknown> {
  const params = parameters(scope)
  return {
    site: scope.site,
    tz: scope.tz,
    filters: scope.filters,
    ...(scope.range === 'custom'
      ? { from: scope.from, to: scope.to }
      : { range: params.get('range') }),
    ...(!funnel && scope.compare !== 'none' ? { compare: scope.compare } : {}),
  }
}

export class ApiClient {
  private active = 0
  private waiting: (() => void)[] = []
  constructor(
    public config: Config,
    private readonly onResponse?: (
      endpoint: string,
      milliseconds: number,
    ) => void,
  ) {}

  private async acquire(signal: AbortSignal) {
    signal.throwIfAborted()
    if (this.active < 3) {
      this.active++
      return
    }
    await new Promise<void>((resolve, reject) => {
      const resume = () => {
        signal.removeEventListener('abort', abort)
        resolve()
      }
      const abort = () => {
        this.waiting = this.waiting.filter((item) => item !== resume)
        reject(signal.reason)
      }
      if (signal.aborted) {
        reject(signal.reason)
        return
      }
      this.waiting.push(resume)
      signal.addEventListener('abort', abort, { once: true })
    })
  }
  async request<T>(
    endpoint: string,
    scope: Scope,
    signal: AbortSignal,
    extra: Record<string, string> = {},
    body?: Record<string, unknown>,
  ): Promise<T> {
    const timeout = AbortSignal.timeout(30_000)
    const combined = AbortSignal.any([signal, timeout])
    let acquired = false
    try {
      await this.acquire(combined)
      acquired = true
      combined.throwIfAborted()
      const benchmark = this.config.benchmark
      const base = normalizeApiUrl(
        benchmark?.siteId === scope.site
          ? benchmark.apiBaseUrl
          : this.config.apiBaseUrl,
      )
      const params =
        endpoint === 'realtime'
          ? new URLSearchParams({ site: scope.site })
          : parameters(scope, extra)
      const started = performance.now()
      const response = await fetch(
        `${base}/${endpoint}${body ? '' : `?${params}`}`,
        {
          method: body ? 'POST' : 'GET',
          credentials: 'include',
          cache: 'no-store',
          signal: combined,
          headers: {
            Accept: 'application/json',
            ...(body ? { 'Content-Type': 'application/json' } : {}),
          },
          ...(body
            ? {
                body: JSON.stringify({
                  ...postScope(scope, endpoint === 'funnel'),
                  ...body,
                }),
              }
            : {}),
        },
      )
      if (response.status === 401 && this.config.mode === 'live')
        redirectToSignIn()
      if (!response.ok) {
        const message = (await response.json().catch(() => null)) as {
          error?: string
        } | null
        throw new Error(
          message?.error ||
            (response.status === 401
              ? 'Your session ended. Sign in again.'
              : response.status === 403
                ? 'Access to this site is not allowed.'
                : `Reporting API returned HTTP ${response.status}.`),
        )
      }
      if (!response.headers.get('content-type')?.includes('application/json'))
        throw new Error(
          'The API returned HTML instead of JSON. Check the API URL and host authentication.',
        )
      const result = (await response.json()) as T
      combined.throwIfAborted()
      this.onResponse?.(endpoint, Math.max(0, performance.now() - started))
      return result
    } catch (error) {
      if (timeout.aborted && !signal.aborted)
        throw new Error(
          'The report timed out after 30 seconds. Try a shorter date range.',
        )
      if (error instanceof TypeError)
        throw new Error(
          'Cannot reach the reporting API. Check the URL, network, and CORS configuration.',
        )
      throw error
    } finally {
      if (acquired) {
        const next = this.waiting.shift()
        // Pass the occupied slot directly to the next waiter.
        if (next) next()
        else this.active--
      }
    }
  }
}
