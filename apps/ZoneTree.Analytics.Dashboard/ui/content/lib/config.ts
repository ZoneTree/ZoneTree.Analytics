import { normalizeApiUrl } from './api'
import { type Config, dashboardConfigPath, defaultConfig } from './contracts'
import { applyUser, redirectToSignIn } from './session'

export function validateConfig(input: Partial<Config>): Config {
  const value = { ...defaultConfig, ...input }
  if (!['demo', 'live'].includes(value.mode))
    throw new Error('Choose demo data or the live API.')
  value.apiBaseUrl = normalizeApiUrl(value.apiBaseUrl)
  new Intl.DateTimeFormat('en', { timeZone: value.timeZone }).format()
  if (![0, 30, 60, 300].includes(Number(value.refreshSeconds)))
    throw new Error('Choose a supported refresh interval.')
  value.refreshSeconds = Number(value.refreshSeconds)
  if (
    !Array.isArray(value.sites) ||
    !value.sites.length ||
    value.sites.some(
      (site) =>
        typeof site.id !== 'string' ||
        typeof site.name !== 'string' ||
        !site.name.trim(),
    )
  )
    throw new Error(
      'Your account has no sites yet. Ask the administrator for access.',
    )
  if (new Set(value.sites.map((site) => site.id)).size !== value.sites.length)
    throw new Error('Each site ID must be unique.')
  if (value.sampleData) {
    const sample = value.sampleData
    if (typeof sample.siteId !== 'string' || !sample.siteId)
      throw new Error('The sample site ID is invalid.')
    sample.apiBaseUrl = normalizeApiUrl(sample.apiBaseUrl)
    sample.managementUrl = normalizeApiUrl(sample.managementUrl)
    if (!value.sites.some((site) => site.id === sample.siteId))
      value.sites = [
        ...value.sites,
        { id: sample.siteId, name: 'Performance samples' },
      ]
    if (sample.benchmark) {
      if (
        typeof sample.benchmark.siteId !== 'string' ||
        !sample.benchmark.siteId ||
        sample.benchmark.siteId === sample.siteId
      )
        throw new Error('The benchmark site ID is invalid.')
      sample.benchmark.apiBaseUrl = normalizeApiUrl(sample.benchmark.apiBaseUrl)
      if (!value.sites.some((site) => site.id === sample.benchmark?.siteId))
        value.sites = [
          ...value.sites,
          { id: sample.benchmark.siteId, name: 'Latest benchmark' },
        ]
    }
  }
  return value
}
export function readStorage(key: string, session = false): unknown {
  try {
    return JSON.parse(
      (session ? sessionStorage : localStorage).getItem(key) || 'null',
    )
  } catch {
    return null
  }
}
export function writeStorage(key: string, value: unknown, session = false) {
  try {
    ;(session ? sessionStorage : localStorage).setItem(
      key,
      JSON.stringify(value),
    )
  } catch {
    /* Private browsers may disable storage. */
  }
}
export async function loadConfig(): Promise<Config> {
  const url = new URL(dashboardConfigPath, location.origin)
  const response = await fetch(url, {
    cache: 'no-store',
    credentials: 'same-origin',
  })
  if (response.status === 401) {
    redirectToSignIn()
    throw new Error('Your session ended. Sign in again.')
  }
  if (!response.ok)
    throw new Error(
      `Cannot load dashboard configuration (${response.status}). Check the host's ${dashboardConfigPath} endpoint.`,
    )
  const supplied = (await response.json()) as Partial<Config>
  applyUser(supplied.user)
  return validateConfig(supplied)
}
export function assetUrl(path: string): URL {
  const basePath = document.body.dataset.basePath || '/'
  return new URL(`${basePath.replace(/\/?$/, '/')}${path}`, location.origin)
}
