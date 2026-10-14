// The camel-case HTTP contract from ReportingResponses.cs (durations are seconds).
export interface Plan {
  complete: boolean
  warnings: string[]
  steps: { event: string; source: string; from: string; to: string }[]
  rollupSuggestions: {
    event: string
    id: string
    dimensions: string[]
    events: string[]
  }[]
}
export interface Summary {
  visitors: number
  pageViews: number
  sessions: number | null
  bounceRate: number | null
  averageSessionDuration: number | null
  pageViewsPerSession: number | null
  botHits: number | null
  previous?: Summary
}
export interface Report<T> {
  from: string
  to: string
  totalRows: number
  rows: T[]
  plan: Plan
}
export interface SummaryReport {
  from: string
  to: string
  summary: Summary
  plan: Plan
}
export interface Point {
  bucket: string
  summary: Summary
}
export interface Breakdown {
  value: string | null
  visitors: number
  pageViews: number
  visitorShare: number | null
  sessions: number | null
  bounceRate: number | null
  averageSessionDuration: number | null
  previous?: Breakdown
}
export interface Page {
  page: string | null
  visitors: number
  pageViews: number
  entrances: number | null
  exits: number | null
  exitRate: number | null
  botHits: number | null
  previous?: Page
}
export interface Goal {
  name: string
  visitors: number
  events: number
  conversionRate: number | null
}
export interface Bot {
  name: string | null
  category: string | null
  hits: number
  share: number | null
  previous?: Bot
}
export interface Realtime {
  visitors: number | null
  minutes: number
}
export interface QueryRow {
  bucket?: string
  dimensions?: Record<string, string | null>
  values: Record<string, number | null>
  previous?: QueryRow
}
export interface QueryResult {
  from: string
  to: string
  timeZone: string
  interval: string
  groupBy: string[]
  metrics: string[]
  totalRows: number
  totals: QueryRow
  rows: QueryRow[]
  plan: Plan
}
export interface FunnelResult {
  from: string
  to: string
  conversionRate: number | null
  steps: {
    name: string
    visitors: number
    conversionRate: number | null
    stepConversionRate: number | null
    dropOff: number
  }[]
  plan: Plan
}
export interface Scope {
  site: string
  range: string
  compare: string
  tz: string
  filters: string[]
  from?: string
  to?: string
}
export interface Config {
  mode: 'demo' | 'live'
  apiBaseUrl: string
  timeZone: string
  sites: { id: string; name: string }[]
  refreshSeconds: number
  sampleData?: SampleDataCapability | null
}
export interface SampleDataCapability {
  siteId: string
  apiBaseUrl: string
  managementUrl: string
  benchmark?: { siteId: string; apiBaseUrl: string }
}
export const dashboardConfigPath = '/_analytics/api/config'
export const dashboardTrackerPath = '/_analytics/script.js'
export const dashboardEventPath = '/_analytics/event'
export const defaultConfig: Config = {
  mode: 'demo',
  apiBaseUrl: '/_analytics/api',
  timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
  sites: [{ id: '', name: 'Default site' }],
  refreshSeconds: 0,
}
