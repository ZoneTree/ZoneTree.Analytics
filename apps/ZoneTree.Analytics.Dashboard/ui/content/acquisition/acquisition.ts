import { defineComponent, html } from 'regor'
import type { Breakdown, Report } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'
import { breakdownTable } from '../lib/reports'

export interface AcquisitionPage {
  dashboard: DashboardModel
  dimensions: { id: string; label: string }[]
}

const acquisitionTemplate = html`<ReportHeader :dashboard="dashboard"/>
<ReportControls :dashboard="dashboard"/>
<ReportFeedback :dashboard="dashboard"/>
<Panel :aria-busy="dashboard.loading" r-if="dashboard.table.headers.length">
  <div class="panel-heading">
    <div>
      <h2>Acquisition breakdown</h2>
      <p>Click a value to focus the report.</p>
    </div>
  </div>
  <div class="tabs" aria-label="Acquisition dimension">
    <Btn
      variant="outline"
      r-for="dimension in dimensions"
      :aria-pressed="dashboard.tab === dimension.id"
      @click="dashboard.setTab(dimension.id)"
    >
      {{ dimension.label }}
    </Btn>
  </div>
  <ReportTable :data="dashboard.table" :onFilter="dashboard.addFilter"/>
  <ReportPagination :dashboard="dashboard"/>
</Panel>
<ReportFooter :dashboard="dashboard"/>`

async function loadBreakdown(model: DashboardModel, signal: AbortSignal) {
  const result = await model.request<Report<Breakdown>>('breakdown', signal, {
    dimension: model.tab(),
    limit: '20',
    offset: String(model.offset()),
  })
  model.table(breakdownTable(result.rows, model.tab()))
  model.totalRows(result.totalRows)
  model.exportRows(model.table().exportRows)
}
export function defineAcquisitionPage(model: DashboardModel) {
  return defineComponent<AcquisitionPage>(acquisitionTemplate, {
    context: () => ({
      dashboard: model,
      dimensions: [
        { id: 'source', label: 'Sources' },
        { id: 'channel', label: 'Channels' },
        { id: 'referrer', label: 'Referrers' },
        { id: 'utm_source', label: 'UTM sources' },
        { id: 'utm_medium', label: 'UTM medium' },
        { id: 'utm_campaign', label: 'Campaigns' },
        { id: 'utm_content', label: 'UTM content' },
        { id: 'utm_term', label: 'UTM terms' },
      ],
    }),
  })
}

void mountReport({
  kind: 'sources',
  selector: '#acquisition-app',
  component: defineAcquisitionPage,
  loader: loadBreakdown,
})
