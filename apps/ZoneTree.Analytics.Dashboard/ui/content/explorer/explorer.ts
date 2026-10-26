import { computed, defineComponent, html, observe, sref } from 'regor'
import type { QueryResult } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'
import { queryPresets, queryTable } from '../lib/reports'

export function createExplorerModel(model: DashboardModel) {
  const preset = sref('traffic')
  const json = sref(JSON.stringify(queryPresets.traffic, null, 2))
  const result = sref<QueryResult | null>(null)
  model.registerCleanup(
    observe(preset, (value) => {
      json(JSON.stringify(queryPresets[value], null, 2))
      result(null)
      model.exportRows([])
    }),
  )
  model.registerCleanup(
    observe(
      computed(() => JSON.stringify(model.scope())),
      () => {
        result(null)
        model.exportRows([])
      },
    ),
  )
  return {
    preset,
    json,
    result,
    presets: [
      { value: 'traffic', label: 'Traffic by source' },
      { value: 'revenue', label: 'Revenue by plan' },
      { value: 'latency', label: 'Page load latency' },
      { value: 'trend', label: 'Visitors & moving average' },
    ],
    table: computed(() => {
      const current = result()
      return current
        ? queryTable(current)
        : { headers: [], rows: [], exportRows: [] }
    }),
    response: computed(() => JSON.stringify(result(), null, 2)),
    totals: computed(() => {
      const current = result()
      if (!current) return { headers: [], rows: [], exportRows: [] }
      return {
        headers: current.metrics,
        rows: [
          {
            cells: current.metrics.map((key) => ({
              text: model.number(current.totals.values[key]),
              numeric: true,
            })),
          },
        ],
        exportRows: [],
      }
    }),
    run: async (event: Event) => {
      event.preventDefault()
      model.exportRows([])
      let body: Record<string, unknown>
      try {
        const parsed: unknown = JSON.parse(json())
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed))
          throw new Error('The query must be a JSON object.')
        body = parsed as Record<string, unknown>
      } catch (failure) {
        model.error((failure as Error).message)
        return
      }
      const response = await model.execute<QueryResult>('query', body)
      if (response) {
        result(response)
        model.exportRows(queryTable(response).exportRows)
      }
    },
  }
}

export interface ExplorerPage {
  dashboard: DashboardModel
  query: ReturnType<typeof createExplorerModel>
}

const explorerTemplate = html`<ReportHeader :dashboard="dashboard"/>
<ReportControls :dashboard="dashboard"/>
<ReportFeedback :dashboard="dashboard"/>
<Panel class="form-panel">
  <h2>Explore your data</h2>
  <p>
    Use the engine's metric expressions, dimensions, intervals, segments, and
    filters. Controls above apply unless your JSON overrides them.
  </p>
  <AppForm @submit.prevent="query.run">
    <FormSelectField
      id="query-preset"
      label="Start from a query"
      :model="query.preset"
      :options="query.presets"
      :disabled="dashboard.loading"/>
    <label for="query-json">
      Query JSON
      <textarea
        id="query-json"
        r-model="query.json"
        rows="13"
        spellcheck="false"
        :disabled="dashboard.loading"
      ></textarea>
    </label>
    <div class="form-actions">
      <Btn
        class="button primary"
        tone="accent"
        type="submit"
        :disabled="dashboard.loading"
      >
        Run query
      </Btn>
      <span class="field-hint">Read-only · POST /query</span>
    </div>
  </AppForm>
</Panel>
<div :aria-busy="dashboard.loading" r-if="query.result">
  <Panel>
    <div class="panel-heading">
      <div>
        <h2>Query results</h2>
        <p>
          {{ dashboard.number(query.result.rows.length) }} of {{
            dashboard.number(query.result.totalRows) }} rows
          returned. Export contains these rows.
        </p>
      </div>
    </div>
    <ReportTable :data="query.table" :onFilter="dashboard.addFilter"/>
  </Panel>
  <Panel class="totals-panel">
    <div class="panel-heading"><h2>Totals</h2></div>
    <ReportTable :data="query.totals" :onFilter="dashboard.addFilter"/>
  </Panel>
  <details class="panel">
    <summary class="panel-heading">Response &amp; query plan</summary>
    <pre class="result-json" r-text="query.response"></pre>
  </details>
</div>
<ReportFooter :dashboard="dashboard"/>`

export function defineExplorerPage(model: DashboardModel) {
  return defineComponent<ExplorerPage>(explorerTemplate, {
    context: () => ({ dashboard: model, query: createExplorerModel(model) }),
  })
}

void mountReport({
  kind: 'explorer',
  selector: '#explorer-app',
  component: defineExplorerPage,
})
