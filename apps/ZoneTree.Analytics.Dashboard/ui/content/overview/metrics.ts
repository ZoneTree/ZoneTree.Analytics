import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  type SRef,
  unref,
} from 'regor'
import type { DashboardModel } from '../lib/dashboard-model'
import { type MetricKey, metricCards } from '../lib/reports'

export interface OverviewMetrics {
  dashboard: DashboardModel | SRef<DashboardModel>
  cards: ComputedRef<ReturnType<typeof metricCards>>
  selectMetric: (key: MetricKey) => void
}

const template = html`<section class="metric-grid" aria-label="Summary metrics">
  <button
    class="metric-card"
    type="button"
    r-for="card in cards"
    :class="{selected:dashboard.metric === card.key}"
    :aria-pressed="dashboard.metric === card.key"
    :disabled="!card.available"
    :title="card.available ? 'Plot ' + card.label : 'Unavailable for these filters or engine configuration'"
    @click="selectMetric(card.key)"
  >
    <span class="metric-label">{{ card.label }}</span>
    <strong class="metric-value">{{ card.value }}</strong>
    <span class="metric-change">
      <span
        class="delta"
        :class="{negative:card.negative}"
        r-if="dashboard.compare !== 'none' && card.hasDelta"
      >
        {{ card.delta }}
      </span>
      <span>{{ dashboard.compare === 'none' ? 'No comparison' : card.hasDelta ?
          dashboard.comparisonLabel : 'No comparison available' }}</span>
    </span>
  </button>
</section>`

export function defineOverviewMetrics() {
  return defineComponent<OverviewMetrics>(template, {
    props: ['dashboard'],
    context: (head) => ({
      ...head.props,
      cards: computed(() =>
        metricCards(unref(head.props.dashboard).summary()?.summary ?? null),
      ),
      selectMetric: (key) => {
        unref(head.props.dashboard).metric(key)
      },
    }),
  })
}
