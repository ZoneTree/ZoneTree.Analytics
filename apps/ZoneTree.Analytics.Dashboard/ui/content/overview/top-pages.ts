import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  type SRef,
  unref,
} from 'regor'
import type { DashboardModel } from '../lib/dashboard-model'

export interface OverviewTopPages {
  dashboard: DashboardModel | SRef<DashboardModel>
  max: ComputedRef<number>
  barWidth: (views: number) => string
  filterPage: (page: string | null) => void
}

const template = html`
  <Panel>
    <div class="panel-heading">
      <div><h2>Top pages</h2><p>The most visited places on your website</p></div>
      <a class="panel-link" :href="dashboard.assetPath('pages/')">Details ↗</a>
    </div>
    <div class="breakdown-list" r-if="dashboard.topPages.length">
      <div class="list-heading"><span>PAGE</span><span>VIEWS</span></div>
      <button
        class="breakdown-row"
        type="button"
        r-for="row in dashboard.topPages"
        :disabled="row.page === null"
        :style="{'--bar-width': barWidth(row.pageViews)}"
        @click="filterPage(row.page)"
      >
        <span class="row-name">{{ row.page || 'Unknown' }}</span>
        <span class="row-count">{{ dashboard.number(row.pageViews) }}</span>
      </button>
    </div>
    <EmptyReport r-else />
  </Panel>
`

export function defineOverviewTopPages() {
  return defineComponent<OverviewTopPages>(template, {
    props: ['dashboard'],
    context: (head) => {
      const max = computed(() =>
        Math.max(
          1,
          ...unref(head.props.dashboard)
            .topPages()
            .map((row) => row.pageViews),
        ),
      )
      return {
        ...head.props,
        max,
        barWidth: (views) =>
          `${Math.min(100, Math.max(0, (views / max()) * 80))}%`,
        filterPage: (page) => {
          unref(head.props.dashboard).addFilter('page', page)
        },
      }
    },
  })
}
