import { defineComponent, html } from 'regor'
import type { Goal, Report } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'
import { goalsTable } from '../lib/reports'

export interface EventsPage {
  dashboard: DashboardModel
}

const eventsTemplate = html`
  <ReportHeader :dashboard="dashboard" />
  <ReportControls :dashboard="dashboard" />
  <ReportFeedback :dashboard="dashboard" />
  <Panel :aria-busy="dashboard.loading" r-if="dashboard.table.headers.length">
    <div class="panel-heading">
      <div>
        <h2>Tracked events &amp; goals</h2>
        <p>
          Conversion rate = goal visitors ÷ visitors with a page view. Events are discovered from
          your tracked data.
        </p>
      </div>
    </div>
    <ReportTable :data="dashboard.table" :onFilter="dashboard.addFilter" />
    <ReportPagination :dashboard="dashboard" />
  </Panel>
  <ReportFooter :dashboard="dashboard" />
`

async function loadGoals(model: DashboardModel, signal: AbortSignal) {
  const result = await model.request<Report<Goal>>('goals', signal, {
    limit: '20',
    offset: String(model.offset()),
  })
  model.table(goalsTable(result.rows))
  model.totalRows(result.totalRows)
  model.exportRows(model.table().exportRows)
}
export function defineEventsPage(model: DashboardModel) {
  return defineComponent<EventsPage>(eventsTemplate, {
    context: () => ({ dashboard: model }),
  })
}

void mountReport({
  kind: 'goals',
  selector: '#events-app',
  component: defineEventsPage,
  loader: loadGoals,
})
