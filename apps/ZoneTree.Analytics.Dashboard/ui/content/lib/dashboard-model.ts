import {
  createDashboardFilters,
  type DashboardFilters,
} from '../components/report-controls'
import { type Config, defaultConfig } from './contracts'
import {
  createDashboardExport,
  type DashboardExport,
} from './dashboard/exports'
import {
  createDashboardRuntime,
  type DashboardLifecycle,
} from './dashboard/lifecycle'
import {
  createDashboardRequests,
  type DashboardRequests,
} from './dashboard/requests'
import { createDashboardScope, type DashboardScope } from './dashboard/scope'
import {
  createDashboardSelection,
  type DashboardSelection,
} from './dashboard/selection'
import {
  createDashboardReports,
  createDashboardStatus,
  type DashboardReports,
  type DashboardStatus,
} from './dashboard/state'
import { number, percent } from './format'
import { countryName, type ReportKind, reportInfo } from './reports'

export type { DashboardFilters } from '../components/report-controls'
export type { DashboardExport } from './dashboard/exports'
export type { DashboardLifecycle } from './dashboard/lifecycle'
export type { DashboardRequests } from './dashboard/requests'
export type { DashboardScope } from './dashboard/scope'
export type { DashboardSelection } from './dashboard/selection'
export type { DashboardReports, DashboardStatus } from './dashboard/state'

export interface DashboardIdentity {
  kind: ReportKind
  title: string
  description: string
}

export interface DashboardFormatting {
  number: typeof number
  percent: typeof percent
  countryName: typeof countryName
  assetPath: (path: string) => string
}

export interface DashboardModel
  extends DashboardIdentity,
    DashboardFormatting,
    DashboardScope,
    DashboardReports,
    DashboardStatus,
    DashboardSelection,
    DashboardFilters,
    DashboardExport,
    DashboardRequests,
    DashboardLifecycle {}

export type Loader = (
  model: DashboardModel,
  signal: AbortSignal,
) => Promise<void>

export function createDashboardModel(
  kind: ReportKind,
  initialConfig: Config = defaultConfig,
): DashboardModel {
  const scope = createDashboardScope(initialConfig)
  const reports = createDashboardReports()
  const status = createDashboardStatus()
  const requests = createDashboardRequests(scope, status)
  const exports = createDashboardExport(kind, scope, status)
  // The runtime reads the composed model only after start/load is called.
  const runtime = createDashboardRuntime({
    scope,
    reports,
    status,
    requests,
    exports,
    getModel: () => model,
  })
  const model: DashboardModel = {
    kind,
    title: reportInfo[kind].title,
    description: reportInfo[kind].description,
    number,
    percent,
    countryName,
    assetPath: (path) =>
      (document.body.dataset.basePath || '/').replace(/\/?$/, '/') + path,
    ...scope,
    ...reports,
    ...status,
    ...requests,
    ...exports,
    ...createDashboardSelection(kind, runtime.queueLoad),
    ...createDashboardFilters(scope, status),
    ...runtime.lifecycle,
  }
  return model
}
