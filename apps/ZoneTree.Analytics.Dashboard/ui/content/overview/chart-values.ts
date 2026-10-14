import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  observe,
  onUnmounted,
  type SRef,
  sref,
  unref,
} from 'regor'
import type { DashboardModel } from '../lib/dashboard-model'
import type { TableModel } from '../lib/reports'

export interface OverviewChartValues {
  dashboard: DashboardModel | SRef<DashboardModel>
  table: ComputedRef<TableModel>
  open: SRef<boolean>
  offset: SRef<number>
  pageLabel: ComputedRef<string>
  hasNext: ComputedRef<boolean>
  paginated: ComputedRef<boolean>
  toggle: (event: Event) => void
  page: (direction: number) => void
}

const template = html`
  <details class="chart-data" r-if="dashboard.points" @toggle="toggle">
    <summary>View exact chart values</summary>
    <ReportTable :data="table" :onFilter="dashboard.addFilter" r-if="open" />
    <div class="pagination" r-if="open && paginated" aria-label="Exact chart values pagination">
      <span>{{ pageLabel }}</span>
      <div>
        <Btn class="button" variant="outline" :disabled="offset === 0" @click="page(-1)">Previous</Btn>
        <Btn class="button" variant="outline" :disabled="!hasNext" @click="page(1)">Next</Btn>
      </div>
    </div>
  </details>
`

export function defineOverviewChartValues() {
  return defineComponent<OverviewChartValues>(template, {
    props: ['dashboard'],
    context: (head) => {
      const dashboard = () => unref(head.props.dashboard)
      const open = sref(false)
      const offset = sref(0)
      const pageSize = 100
      const count = computed(() => dashboard().points()?.rows.length ?? 0)
      const stop = observe(dashboard().points, () => offset(0))
      onUnmounted(stop)
      return {
        ...head.props,
        open,
        offset,
        toggle: (event: Event) =>
          open((event.currentTarget as HTMLDetailsElement).open),
        page: (direction: number) =>
          offset(
            Math.max(
              0,
              Math.min(
                Math.max(0, Math.ceil(count() / pageSize) - 1) * pageSize,
                offset() + direction * pageSize,
              ),
            ),
          ),
        paginated: computed(() => count() > pageSize),
        hasNext: computed(() => offset() + pageSize < count()),
        pageLabel: computed(
          () =>
            `${offset() + 1}–${Math.min(offset() + pageSize, count())} of ${count().toLocaleString('en')} rows`,
        ),
        table: computed<TableModel>(() => {
          if (!open()) return { headers: [], rows: [], exportRows: [] }
          const dashboard = unref(head.props.dashboard)
          const key = dashboard.metric()
          const selected = dashboard.selectedMetric()
          const comparison = dashboard.compare() !== 'none'
          const timeZone =
            dashboard.config().mode === 'demo'
              ? 'UTC'
              : dashboard.config().timeZone
          const dates = new Intl.DateTimeFormat('en', {
            dateStyle: 'short',
            timeStyle: 'medium',
            timeZone,
          })
          return {
            headers: [
              'Time',
              selected.label,
              ...(comparison ? ['Comparison'] : []),
            ],
            rows: (dashboard.points()?.rows ?? [])
              .slice(offset(), offset() + pageSize)
              .map((point) => ({
                cells: [
                  {
                    text: dates.format(new Date(point.bucket)),
                  },
                  { text: selected.format(point.summary[key]), numeric: true },
                  ...(comparison
                    ? [
                        {
                          text: selected.format(point.summary.previous?.[key]),
                          numeric: true,
                        },
                      ]
                    : []),
                ],
              })),
            exportRows: [],
          }
        }),
      }
    },
  })
}
