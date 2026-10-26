import { defineComponent, html } from 'regor'
import type { Page, Report } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'
import { pagesTable } from '../lib/reports'

export interface PagesPage {
  dashboard: DashboardModel
}

const pagesTemplate = html`<ReportHeader :dashboard="dashboard"/>
<ReportControls :dashboard="dashboard"/>
<ReportFeedback :dashboard="dashboard"/>
<Panel :aria-busy="dashboard.loading" r-if="dashboard.table.headers.length">
  <div class="panel-heading">
    <div>
      <h2>Page performance</h2>
      <p>Session metrics unavailable for these filters appear as —.</p>
    </div>
  </div>
  <div class="tabs" aria-label="Page report">
    <Btn
      variant="outline"
      :aria-pressed="dashboard.tab === 'pages'"
      @click="dashboard.setTab('pages')"
    >
      All pages
    </Btn>
    <Btn
      variant="outline"
      :aria-pressed="dashboard.tab === 'exit-pages'"
      @click="dashboard.setTab('exit-pages')"
    >
      Exit pages
    </Btn>
  </div>
  <ReportTable :data="dashboard.table" :onFilter="dashboard.addFilter"/>
  <ReportPagination :dashboard="dashboard"/>
</Panel>
<ReportFooter :dashboard="dashboard"/>`

async function loadPages(model: DashboardModel, signal: AbortSignal) {
  const result = await model.request<Report<Page>>(
    model.tab() === 'exit-pages' ? 'exit-pages' : 'pages',
    signal,
    { limit: '20', offset: String(model.offset()) },
  )
  model.table(pagesTable(result.rows))
  model.totalRows(result.totalRows)
  model.exportRows(model.table().exportRows)
}
export function definePagesPage(model: DashboardModel) {
  return defineComponent<PagesPage>(pagesTemplate, {
    context: () => ({ dashboard: model }),
  })
}

void mountReport({
  kind: 'pages',
  selector: '#pages-app',
  component: definePagesPage,
  loader: loadPages,
})
