import { batch, defineComponent, html } from 'regor'
import type {
  Breakdown,
  Page,
  Point,
  Report,
  SummaryReport,
} from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'
import { defineOverviewBreakdowns } from './breakdowns'
import { defineOverviewChartValues } from './chart-values'
import { defineOverviewDevices } from './devices'
import { defineOverviewMetrics } from './metrics'
import { defineOverviewTopPages } from './top-pages'
import { defineOverviewTraffic } from './traffic'

export interface OverviewPage {
  dashboard: DashboardModel
}

const overviewTemplate = html`<ReportHeader :dashboard="dashboard"/>
<ReportControls :dashboard="dashboard"/>
<ReportFeedback :dashboard="dashboard"/>
<div
  :aria-busy="dashboard.loading"
  r-if="dashboard.summary"
>
  <OverviewMetrics :dashboard="dashboard"/>
  <OverviewTraffic :dashboard="dashboard"/>
  <OverviewBreakdowns :dashboard="dashboard"/>
</div>
<EmptyReport
  title="Report unavailable"
  description="Review the message above and try refreshing your reports."
  r-if="!dashboard.loading && dashboard.error && !dashboard.summary"/>
<ReportFooter :dashboard="dashboard"/>`
async function loadOverview(model: DashboardModel, signal: AbortSignal) {
  const reports = await Promise.allSettled([
    model.request<SummaryReport>('summary', signal),
    model.request<Report<Point>>('timeseries', signal, {
      interval: model.interval(),
    }),
    model.request<Report<Page>>('pages', signal, { limit: '5' }),
    model.request<Report<Breakdown>>('breakdown', signal, {
      dimension: 'source',
      limit: '5',
    }),
    model.request<Report<Breakdown>>('breakdown', signal, {
      dimension: 'country',
      limit: '5',
    }),
    model.request<Report<Breakdown>>('breakdown', signal, {
      dimension: 'device',
      limit: '10',
    }),
  ])
  signal.throwIfAborted()
  const [summary, timeseries, pages, sources, countries, devices] = reports
  if (summary.status === 'rejected') throw summary.reason
  batch(() => {
    model.summary(summary.value)
    if (summary.value.summary[model.metric()] == null) model.metric('visitors')
    if (timeseries.status === 'fulfilled') model.points(timeseries.value)
    if (pages.status === 'fulfilled') model.topPages(pages.value.rows)
    if (sources.status === 'fulfilled') model.sources(sources.value.rows)
    if (countries.status === 'fulfilled') model.countries(countries.value.rows)
    if (devices.status === 'fulfilled') model.devices(devices.value.rows)
    const names = [
      'Summary',
      'Time series',
      'Pages',
      'Sources',
      'Countries',
      'Devices',
    ]
    reports.forEach((result, index) => {
      if (result.status === 'rejected')
        model.warnings([
          ...model.warnings(),
          `${names[index]} could not load: ${result.reason instanceof Error ? result.reason.message : String(result.reason)}`,
        ])
    })
    if (timeseries.status === 'fulfilled')
      model.exportRows([
        [
          'Time',
          'Visitors',
          'Page views',
          'Sessions',
          'Bounce rate',
          'Avg. duration (seconds)',
          'Previous visitors',
        ],
        ...timeseries.value.rows.map((row) => [
          row.bucket,
          row.summary.visitors,
          row.summary.pageViews,
          row.summary.sessions,
          row.summary.bounceRate,
          row.summary.averageSessionDuration,
          row.summary.previous?.visitors,
        ]),
      ])
  })
}
export function defineOverviewPage(model: DashboardModel) {
  return defineComponent<OverviewPage>(overviewTemplate, {
    context: () => ({ dashboard: model }),
  })
}

void mountReport({
  kind: 'overview',
  selector: '#overview-app',
  component: defineOverviewPage,
  loader: loadOverview,
  components: {
    OverviewMetrics: defineOverviewMetrics(),
    OverviewTraffic: defineOverviewTraffic(),
    OverviewChartValues: defineOverviewChartValues(),
    OverviewBreakdowns: defineOverviewBreakdowns(),
    OverviewTopPages: defineOverviewTopPages(),
    OverviewDevices: defineOverviewDevices(),
  },
})
