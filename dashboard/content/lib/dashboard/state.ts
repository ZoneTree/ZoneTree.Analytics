import { type SRef, sref } from 'regor'
import type {
  Breakdown,
  Page,
  Point,
  Report,
  SummaryReport,
} from '../contracts'
import type { TableModel } from '../reports'

export interface DashboardReports {
  summary: SRef<SummaryReport | null>
  points: SRef<Report<Point> | null>
  topPages: SRef<Page[]>
  sources: SRef<Breakdown[]>
  countries: SRef<Breakdown[]>
  devices: SRef<Breakdown[]>
  table: SRef<TableModel>
  totalRows: SRef<number>
  realtime: SRef<number | null>
}

export interface DashboardStatus {
  loading: SRef<boolean>
  error: SRef<string>
  warnings: SRef<string[]>
  updatedAt: SRef<string>
  apiResponseMs: SRef<number | null>
}

export function emptyReportTable(): TableModel {
  return { headers: [], rows: [], exportRows: [] }
}

export function createDashboardReports(): DashboardReports {
  return {
    summary: sref<SummaryReport | null>(null),
    points: sref<Report<Point> | null>(null),
    topPages: sref<Page[]>([]),
    sources: sref<Breakdown[]>([]),
    countries: sref<Breakdown[]>([]),
    devices: sref<Breakdown[]>([]),
    table: sref<TableModel>(emptyReportTable()),
    totalRows: sref(0),
    realtime: sref<number | null>(null),
  }
}

export function createDashboardStatus(): DashboardStatus {
  return {
    loading: sref(false),
    error: sref(''),
    warnings: sref<string[]>([]),
    updatedAt: sref(''),
    apiResponseMs: sref<number | null>(null),
  }
}

