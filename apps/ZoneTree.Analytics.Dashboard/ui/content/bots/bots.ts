import { defineComponent, html } from 'regor'
import type { Bot, Report } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'
import { botsTable } from '../lib/reports'

export interface BotsPage {
  dashboard: DashboardModel
}

const botsTemplate = html`<ReportHeader :dashboard="dashboard"/>
<ReportControls :dashboard="dashboard"/>
<ReportFeedback :dashboard="dashboard"/>
<Panel :aria-busy="dashboard.loading" r-if="dashboard.table.headers.length">
  <div class="panel-heading">
    <div>
      <h2>Crawler activity</h2>
      <p>
        Requires Bots = BotTraffic.TrackSeparately on your host. Bots never
        count as visitors. Remove audience filters to query bot traffic.
      </p>
    </div>
  </div>
  <ReportTable :data="dashboard.table" :onFilter="dashboard.addFilter"/>
  <ReportPagination :dashboard="dashboard"/>
</Panel>
<ReportFooter :dashboard="dashboard"/>`

async function loadBots(model: DashboardModel, signal: AbortSignal) {
  const result = await model.request<Report<Bot>>('bots', signal, {
    limit: '20',
    offset: String(model.offset()),
  })
  model.table(botsTable(result.rows))
  model.totalRows(result.totalRows)
  model.exportRows(model.table().exportRows)
}

export function defineBotsPage(model: DashboardModel) {
  return defineComponent<BotsPage>(botsTemplate, {
    context: () => ({ dashboard: model }),
  })
}

void mountReport({
  kind: 'bots',
  selector: '#bots-app',
  component: defineBotsPage,
  loader: loadBots,
})
