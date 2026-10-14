import {
  defineLineChartComponents,
  type LineChart,
} from '@purestack/ts-components'
import { type Component, computed, type SRef, unref } from 'regor'

export interface TimelineChart extends LineChart {
  positions?: number[][] | SRef<number[][]>
}

/** Reuse PureStack's SVG template, axes and scale; retain sampled points' time positions. */
export function defineTimelineChart(): { lineChart: Component<TimelineChart> } {
  const native = defineLineChartComponents().lineChart
  return {
    lineChart: {
      ...native,
      props: [...(native.props ?? []), 'positions'],
      context: (head) => {
        const chart = native.context(head)
        const nativeSeries = chart.chartSeries ?? (() => [])
        const chartSeries = computed(() =>
          nativeSeries().map((series, index) => {
            const positions = unref(head.props.positions)?.[index] ?? []
            const points = series.points.map((point, i) => ({
              ...point,
              x:
                8 +
                86 *
                  (positions[i] ?? i / Math.max(1, series.points.length - 1)),
            }))
            const path = points
              .map(
                (point, i) =>
                  `${i ? 'L' : 'M'} ${point.x.toFixed(3)} ${point.y.toFixed(3)}`,
              )
              .join(' ')
            const zero = (chart.zeroLineY?.() ?? 49).toFixed(3)
            return {
              ...series,
              points,
              path,
              areaPath: points.length
                ? `M ${points[0].x.toFixed(3)} ${zero} ${path.replace(/^M /, 'L ')} L ${points[points.length - 1].x.toFixed(3)} ${zero} Z`
                : '',
            }
          }),
        )
        return {
          ...chart,
          chartSeries,
          xLabels: computed(() => {
            const points = chartSeries()[0]?.points ?? []
            const step = Math.max(1, Math.ceil(points.length / 7))
            return points
              .filter((_, i) => i % step === 0 || i === points.length - 1)
              .map((point) => ({ label: point.label, x: point.x, y: 60 }))
          }),
        }
      },
    },
  }
}
