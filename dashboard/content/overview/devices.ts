import {
  type DoughnutChartSegment,
  defineDoughnutChartComponents,
} from '@purestack/ts-components'
import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  type SRef,
  unref,
} from 'regor'
import type { DashboardModel } from '../lib/dashboard-model'

export interface OverviewDevices {
  dashboard: DashboardModel | SRef<DashboardModel>
  components: ReturnType<typeof defineDoughnutChartComponents>
  segments: ComputedRef<DoughnutChartSegment[]>
  total: ComputedRef<string>
  totalSize: ComputedRef<number>
  share: (views: number) => string
  color: (index: number) => string
}

const colors = [
  'var(--zt-chart-current)',
  'var(--zt-chart-previous)',
  'var(--zt-chart-gold)',
  'var(--zt-chart-rose)',
]
const template = html`
  <Panel>
    <div class="panel-heading">
      <div><h2>Devices</h2><p>How people browse your website</p></div>
      <a class="panel-link" :href="dashboard.assetPath('audience/')">Details ↗</a>
    </div>
    <div class="device-layout" r-if="dashboard.devices.length">
      <DoughnutChart
        class="device-chart"
        :segments="segments"
        centerLabel="page views"
        :centerValue="total"
        :centerValueSize="totalSize"
        :centerLabelSize="4.5"
        size="160"
        :thickness="15"
        :animated="false"
        ariaLabel="Page views by device"
        variant="none" />
      <div class="device-legend">
        <div r-for="(row,index) in dashboard.devices">
          <i :style="{background:color(index)}"></i>
          <span>{{ row.value || 'Unknown' }}</span>
          <strong>{{ share(row.pageViews) }}</strong>
        </div>
      </div>
    </div>
    <EmptyReport r-else />
  </Panel>
`

export function defineOverviewDevices() {
  return defineComponent<OverviewDevices>(template, {
    props: ['dashboard'],
    context: (head) => {
      const dashboard = () => unref(head.props.dashboard)
      const views = computed(() =>
        dashboard()
          .devices()
          .reduce((sum, row) => sum + row.pageViews, 0),
      )
      const color = (index: number) => colors[index % colors.length]
      const total = computed(() => dashboard().number(views()))
      return {
        ...head.props,
        components: defineDoughnutChartComponents(),
        color,
        segments: computed<DoughnutChartSegment[]>(() =>
          dashboard()
            .devices()
            .map((row, index) => ({
              label: row.value || 'Unknown',
              value: row.pageViews,
              color: color(index),
            })),
        ),
        total,
        // Font sizes use the chart's 100-unit viewBox. Leave room inside the ring
        // for the full formatted total, including separators on large counts.
        totalSize: computed(() => Math.min(10, 80 / total().length)),
        share: (value) => dashboard().percent(value / (views() || 1)),
      }
    },
  })
}
