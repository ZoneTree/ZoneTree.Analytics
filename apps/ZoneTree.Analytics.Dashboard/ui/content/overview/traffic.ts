import type { LineChartSeries } from '@purestack/ts-components'
import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  onMounted,
  onUnmounted,
  type SRef,
  sref,
  unref,
} from 'regor'
import type { Point } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import type { MetricKey } from '../lib/reports'
import { chartPointBudget, sampleChartIndices } from './chart-sampling'
import { defineTimelineChart } from './timeline-chart'

export interface TrafficSeries {
  series: LineChartSeries[]
  positions: number[][]
}

export function prepareTrafficSeries(
  rows: readonly Point[],
  key: MetricKey,
  title: string,
  compare: boolean,
  hourly: boolean,
  timeZone: string,
  budget: number,
): TrafficSeries {
  const count = compare && rows.some((point) => point.summary.previous) ? 2 : 1
  const value = (point: Point, series: number) => {
    const result =
      series === 0 ? point.summary[key] : point.summary.previous?.[key]
    return typeof result === 'number' && Number.isFinite(result) ? result : null
  }
  const indices = sampleChartIndices(rows, budget, count, value)
  const dates = new Intl.DateTimeFormat('en', {
    month: 'short',
    day: 'numeric',
    ...(hourly ? ({ hour: 'numeric' } as const) : {}),
    timeZone,
  })
  const start = Date.parse(rows[0]?.bucket ?? '')
  const end = Date.parse(rows.at(-1)?.bucket ?? '')
  const multiplier = key === 'bounceRate' ? 100 : 1
  const result: TrafficSeries = { series: [], positions: [] }
  for (let series = 0; series < count; series++) {
    const points: { label: string; value: number }[] = []
    const positions: number[] = []
    for (const index of indices) {
      const current = value(rows[index], series)
      const time = Date.parse(rows[index].bucket)
      if (current == null || !Number.isFinite(time)) continue
      points.push({ label: dates.format(time), value: current * multiplier })
      positions.push(
        end > start
          ? (time - start) / (end - start)
          : index / Math.max(1, rows.length - 1),
      )
    }
    // PureStack omits empty series; keep positions in the same order.
    if (!points.length) continue
    result.series.push({
      label: series ? 'Comparison' : title,
      color: series ? 'var(--zt-chart-previous)' : 'var(--zt-chart-current)',
      points,
    })
    result.positions.push(positions)
  }
  return result
}

export interface OverviewTraffic {
  dashboard: DashboardModel | SRef<DashboardModel>
  components: ReturnType<typeof defineTimelineChart>
  chartElement: SRef<HTMLDivElement | null>
  timeZone: ComputedRef<string>
  rangeLabel: ComputedRef<string>
  series: ComputedRef<LineChartSeries[]>
  positions: ComputedRef<number[][]>
  showMarkers: ComputedRef<boolean>
  valueSuffix: ComputedRef<string>
}

const template = html`<Panel class="traffic-panel">
  <div class="panel-heading">
    <div>
      <h2>{{ dashboard.selectedMetric.label }} over time</h2>
      <p>{{ rangeLabel }} · {{ timeZone }}</p>
    </div>
    <div class="chart-controls" aria-label="Chart interval">
      <button
        type="button"
        :aria-pressed="dashboard.interval === 'day'"
        @click="dashboard.setInterval('day')"
      >
        Daily
      </button>
      <button
        type="button"
        :aria-pressed="dashboard.interval === 'hour'"
        @click="dashboard.setInterval('hour')"
      >
        Hourly
      </button>
    </div>
  </div>
  <div class="chart-wrap" :ref="chartElement">
    <LineChart
      :series="series"
      :positions="positions"
      :ariaLabel="dashboard.selectedMetric.label + ' over the selected range'"
      :description="rangeLabel"
      width="100%"
      height="100%"
      :valueSuffix="valueSuffix"
      :showArea="true"
      :showPoints="showMarkers"
      :animated="false"
      curve="linear"
      variant="none"/>
  </div>
  <div class="chart-legend">
    <span><i class="legend-mark"></i>{{ dashboard.selectedMetric.label }}</span>
    <span r-if="dashboard.compare !== 'none'">
      <i class="legend-mark previous"></i>
      {{ dashboard.compare === 'previous-year' ? 'Previous year' : 'Previous period' }}
    </span>
  </div>
  <OverviewChartValues :dashboard="dashboard"/>
</Panel>`

export function defineOverviewTraffic() {
  return defineComponent<OverviewTraffic>(template, {
    props: ['dashboard'],
    context: (head) => {
      const dashboard = () => unref(head.props.dashboard)
      const chartElement = sref<HTMLDivElement | null>(null)
      const budget = sref(384)
      let resizeObserver: ResizeObserver | undefined
      onMounted(() => {
        const element = chartElement()
        if (!element) return
        const resize = () => {
          const { width, height } = element.getBoundingClientRect()
          if (width > 0 && height > 0) budget(chartPointBudget(width, height))
        }
        resize()
        resizeObserver = new ResizeObserver(resize)
        resizeObserver.observe(element)
      })
      onUnmounted(() => resizeObserver?.disconnect())
      const timeZone = computed(() => {
        const config = dashboard().config()
        return config.mode === 'demo' ? 'UTC' : config.timeZone
      })
      const prepared = computed(() => {
        const model = dashboard()
        return prepareTrafficSeries(
          model.points()?.rows ?? [],
          model.metric(),
          model.selectedMetric().label,
          model.compare() !== 'none',
          model.interval() === 'hour',
          timeZone(),
          budget(),
        )
      })
      return {
        ...head.props,
        components: defineTimelineChart(),
        chartElement,
        series: computed(() => prepared().series),
        positions: computed(() => prepared().positions),
        showMarkers: computed(
          () =>
            prepared().series.length > 0 &&
            prepared().series.every(
              (series) =>
                Array.isArray(series.points) && series.points.length === 1,
            ),
        ),
        timeZone,
        rangeLabel: computed(() => {
          const summary = dashboard().summary()
          if (!summary) return ''
          const from = new Date(summary.from).toLocaleDateString('en', {
            month: 'short',
            day: 'numeric',
            timeZone: timeZone(),
          })
          const to = new Date(+new Date(summary.to) - 1).toLocaleDateString(
            'en',
            {
              month: 'short',
              day: 'numeric',
              year: 'numeric',
              timeZone: timeZone(),
            },
          )
          return `${from} – ${to}`
        }),
        valueSuffix: computed<string>(() => {
          const key = dashboard().metric()
          return key === 'bounceRate'
            ? '%'
            : key === 'averageSessionDuration'
              ? 's'
              : ''
        }),
      }
    },
  })
}
