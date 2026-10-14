import { type ComputedRef, computed, type SRef, sref } from 'regor'
import { type MetricKey, metricDefinitions, type ReportKind } from '../reports'

export interface DashboardSelection {
  offset: SRef<number>
  tab: SRef<string>
  interval: SRef<string>
  metric: SRef<MetricKey>
  selectedMetric: ComputedRef<(typeof metricDefinitions)[number]>
  setTab: (value: string) => void
  setInterval: (value: string) => void
  page: (direction: number) => void
}

export function createDashboardSelection(
  kind: ReportKind,
  reload: () => void,
): DashboardSelection {
  const offset = sref(0)
  const tab = sref<string>(
    kind === 'pages' ? 'pages' : kind === 'audience' ? 'country' : 'source',
  )
  const interval = sref('day')
  const metric = sref<MetricKey>('visitors')
  return {
    offset,
    tab,
    interval,
    metric,
    selectedMetric: computed(
      () =>
        metricDefinitions.find((item) => item.key === metric()) ||
        metricDefinitions[0],
    ),
    setTab: (value) => {
      tab(value)
      offset(0)
      reload()
    },
    setInterval: (value) => {
      interval(value)
      reload()
    },
    page: (direction) => {
      offset(Math.max(0, offset() + direction * 20))
      reload()
    },
  }
}
