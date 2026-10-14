import { defineComponent, html } from 'regor'
import type { Breakdown, Report } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'
import { breakdownTable } from '../lib/reports'

export interface AudiencePage {
  dashboard: DashboardModel
  dimensions: { id: string; label: string }[]
}

const audienceTemplate = html`
  <ReportHeader :dashboard="dashboard" />
  <ReportControls :dashboard="dashboard" />
  <ReportFeedback :dashboard="dashboard" />
  <Panel :aria-busy="dashboard.loading" r-if="dashboard.table.headers.length">
    <div class="panel-heading">
      <div>
        <h2>Audience breakdown</h2>
        <p>Geography depends on the geolocator configured by your host.</p>
      </div>
    </div>
    <div class="tabs" aria-label="Audience dimension">
      <Btn
        variant="outline"
        r-for="dimension in dimensions"
        :aria-pressed="dashboard.tab === dimension.id"
        @click="dashboard.setTab(dimension.id)"
      >
        {{ dimension.label }}
      </Btn>
    </div>
    <ReportTable :data="dashboard.table" :onFilter="dashboard.addFilter" />
    <ReportPagination :dashboard="dashboard" />
  </Panel>
  <ReportFooter :dashboard="dashboard" />
`

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
export function defineAudiencePage(model: DashboardModel) {
  return defineComponent<AudiencePage>(audienceTemplate, {
    context: () => ({
      dashboard: model,
      dimensions: [
        { id: 'country', label: 'Countries' },
        { id: 'region', label: 'Regions' },
        { id: 'city', label: 'Cities' },
        { id: 'device', label: 'Devices' },
        { id: 'browser', label: 'Browsers' },
        { id: 'os', label: 'Operating systems' },
      ],
    }),
  })
}

void mountReport({
  kind: 'audience',
  selector: '#audience-app',
  component: defineAudiencePage,
  loader: loadBreakdown,
})
