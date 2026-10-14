import { type ComputedRef, computed, type SRef, sref } from 'regor'
import { parameters } from '../api'
import { readStorage, writeStorage } from '../config'
import type { Config, Scope } from '../contracts'

export interface DashboardScope {
  config: SRef<Config>
  site: SRef<string>
  range: SRef<string>
  compare: SRef<string>
  filters: SRef<string[]>
  dateFrom: SRef<string>
  dateTo: SRef<string>
  siteOptions: ComputedRef<{ value: string; label: string }[]>
  comparisonLabel: ComputedRef<string>
  scope: () => Scope
}

export function createDashboardScope(initialConfig: Config): DashboardScope {
  const config = sref(initialConfig)
  const saved = readStorage(
    'zt-dashboard-scope-v1',
    true,
  ) as Partial<Scope> | null
  const params = new URLSearchParams(location.search)
  const initialRange = params.get('range') || saved?.range || '30d'
  const range = sref(
    [
      'today',
      'yesterday',
      '7d',
      '30d',
      'this-month',
      'last-month',
      'this-year',
      'custom',
    ].includes(initialRange)
      ? initialRange
      : '30d',
  )
  const site = sref(
    params.get('site') ?? saved?.site ?? initialConfig.sites[0].id,
  )
  const compare = sref(
    params.get('compare') || saved?.compare || 'previous-period',
  )
  if (!['previous-period', 'previous-year', 'none'].includes(compare()))
    compare('previous-period')
  const filters = sref<string[]>(
    params.has('filter')
      ? params.getAll('filter')
      : Array.isArray(saved?.filters)
        ? saved.filters.filter((item) => typeof item === 'string')
        : [],
  )
  const dateFrom = sref(
    params.get('from') || saved?.from || new Date().toISOString().slice(0, 10),
  )
  const dateTo = sref(
    params.get('to') || saved?.to || new Date().toISOString().slice(0, 10),
  )
  return {
    config,
    site,
    range,
    compare,
    filters,
    dateFrom,
    dateTo,
    scope: () => ({
      site: site(),
      range: range(),
      compare: compare(),
      tz: config().timeZone,
      filters: [...filters()],
      ...(range() === 'custom' ? { from: dateFrom(), to: dateTo() } : {}),
    }),
    siteOptions: computed(() => {
      const items = config().sites.map((item) => ({
        value: item.id,
        label: item.name,
      }))
      return items.some((item) => item.value === site())
        ? items
        : [...items, { value: site(), label: site() || 'Default site' }]
    }),
    comparisonLabel: computed<string>(() =>
      compare() === 'previous-year'
        ? 'vs. previous year'
        : compare() === 'none'
          ? 'No comparison'
          : 'vs. previous period',
    ),
  }
}

export function persistDashboardScope(scope: Scope) {
  writeStorage('zt-dashboard-scope-v1', scope, true)
  const url = new URL(location.href)
  url.search = parameters(scope).toString()
  if (scope.compare === 'none') url.searchParams.set('compare', 'none')
  history.replaceState(null, '', url)
}
