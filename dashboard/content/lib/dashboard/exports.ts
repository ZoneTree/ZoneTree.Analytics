import { type SRef, sref } from 'regor'
import { csv } from '../format'
import type { ReportKind } from '../reports'
import type { DashboardScope } from './scope'
import type { DashboardStatus } from './state'

export interface DashboardExport {
  exportRows: SRef<unknown[][]>
  exportCsv: () => void
}

export function createDashboardExport(
  kind: ReportKind,
  scope: DashboardScope,
  status: DashboardStatus,
): DashboardExport {
  const exportRows = sref<unknown[][]>([])
  return {
    exportRows,
    exportCsv: () => {
      if (!exportRows().length || status.loading()) return
      const blob = new Blob([csv(exportRows())], {
        type: 'text/csv;charset=utf-8',
      })
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `zonetree-${kind}-${scope.range()}-${scope.config().mode}.csv`
      link.click()
      setTimeout(() => URL.revokeObjectURL(url), 1000)
    },
  }
}
