// The camel-case contract of the dashboard server's /_dashboard/api endpoints.
import type { UserRole } from './contracts'

export interface UserView {
  id: string
  userName: string
  displayName: string
  role: UserRole
  allSites: boolean
  sites: string[]
  disabled: boolean
  created: string
  lastSignIn: string | null
}
export interface SiteView {
  id: string
  name: string
}
export interface MeResponse {
  user: UserView
  sites: SiteView[]
  sessionId: string
}
export interface SessionView {
  id: string
  created: string
  lastSeen: string
  expires: string
  ipAddress: string | null
  userAgent: string | null
  current: boolean
}
export interface AuditEntry {
  id: string
  time: string
  actor: string | null
  action: string
  detail: string | null
  ipAddress: string | null
}

export type SettingTier = 'Live' | 'Reopen' | 'Restart' | 'Fixed'
export interface SettingOption {
  value: string | number
  label: string
}
export interface SettingField {
  key: string
  label: string
  help: string | null
  type: string
  unit: string | null
  min: number | null
  max: number | null
  nullable: boolean
  emptyLabel: string | null
  options: SettingOption[] | null
  tier: SettingTier
  advanced: boolean
  keyLabel: string | null
  valueLabel: string | null
  fields: SettingField[] | null
}
export interface SettingSection {
  key: string
  label: string
  help: string | null
  fields: SettingField[]
}
export interface SettingsPreset {
  group: string
  key: string
  label: string
  description: string
  values: Record<string, unknown>
}
export interface SettingsSchema {
  sections: SettingSection[]
  presets: SettingsPreset[]
}

export interface SiteSettings {
  id: string
  name: string
  domains: string[]
}
export interface RollupDefinitionSettings {
  dimensions: string[]
  events: string[] | null
}
export interface ValueAggregateSettings {
  value: string
  percentiles: boolean
  firstAndLast: boolean
}
export interface RollupIndexSettings {
  dimension: string
  groupDimensions: string[]
}
// The settings document: sections of plain values, edited by schema key.
export type DashboardSettings = Record<string, Record<string, unknown>> & {
  general: { timeZone: string; dataDirectory: string } & Record<string, unknown>
  sites: {
    items: SiteSettings[]
    primarySite: string
    unmatchedTraffic: string
    trackDashboardUsage: boolean
  }
  rollups: {
    definitions: RollupDefinitionSettings[]
    valueAggregates: ValueAggregateSettings[]
  } & Record<string, unknown>
  indexes: {
    dimensions: string[]
    rollups: RollupIndexSettings[]
  } & Record<string, unknown>
}
export interface ChangeView {
  key: string
  label: string
  tier: SettingTier
}
export interface EngineStatus {
  state: 'NotConfigured' | 'Opening' | 'Running' | 'Reopening' | 'Failed'
  error: string | null
  openedAt: string | null
  indexMaintenance: string | null
  eventsAccepted: number
  eventsProcessed: number
  eventsDropped: number
  queueLength: number
  errors: number
  openPartitions: number
  partitions: { directory: string; records: number | null }[]
  ipLocation: {
    databaseType: string | null
    buildTime: string | null
    ranges: number
    precision: string | null
    lastChecked: string | null
    lastError: string | null
  } | null
}
export interface AdminStatus {
  engine: EngineStatus
  restartRequired: boolean
  dataDirectory: string | null
  systemDirectory: string
  users: number
  sites: number
}
