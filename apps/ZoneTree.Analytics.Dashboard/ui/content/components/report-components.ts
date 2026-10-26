import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  type RefOrValue,
  unref,
} from 'regor'
import type { Breakdown } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import type { number, percent } from '../lib/format'
import type { countryName, TableModel } from '../lib/reports'
import { defineReportControlsComponent } from './report-controls'

export interface ReportHeader {
  dashboard: RefOrValue<DashboardModel>
}
export interface ReportFeedback extends ReportHeader {}
export interface ReportFooter extends ReportHeader {}
export interface ReportPagination extends ReportHeader {}
export interface EmptyReport {
  title?: RefOrValue<string>
  description?: RefOrValue<string>
}
export interface ReportTable {
  data: RefOrValue<TableModel>
  onFilter: (dimension: string, value: string | null) => void
}
export interface ReportBreakdown {
  title: RefOrValue<string>
  subtitle: RefOrValue<string>
  href: RefOrValue<string>
  column?: RefOrValue<string>
  rows: RefOrValue<Breakdown[]>
  dimension: RefOrValue<string>
  onFilter: (dimension: string, value: string | null) => void
  countryName: typeof countryName
  number: typeof number
  percent: typeof percent
  max: ComputedRef<number>
}

// Declarative Regor templates. Report values are bound as text/props, never HTML.
const reportHeader = html`<div class="page-heading">
  <div>
    <div class="eyebrow">YOUR WEBSITE, AT A GLANCE</div>
    <h1>{{ dashboard.title }}</h1>
    <p>{{ dashboard.description }}</p>
  </div>
  <div class="heading-actions">
    <span class="report-loading" role="status" r-if="dashboard.loading">
      <progress aria-label="Loading reports"></progress>
      <span>Loading reports…</span>
    </span>
    <div class="report-badges">
      <Badge
        class="api-response-badge"
        tone="neutral"
        variant="outline"
        title="Latest reporting API response, including network transfer and JSON decoding."
        :aria-label="'API response time: ' + dashboard.apiResponseMs + ' milliseconds'"
        r-if="dashboard.config.mode === 'live' && dashboard.apiResponseMs !== null"
      >
        {{ dashboard.apiResponseMs }} ms
      </Badge>
      <Badge
        class="mode-badge"
        :tone="dashboard.config.mode === 'demo' ? 'warning' : 'success'"
        variant="surface"
      >
        {{ dashboard.config.mode === 'demo' ? 'Demo data' : 'Live API' }}
      </Badge>
    </div>
    <span class="live-count" r-if="dashboard.kind !== 'benchmark'">
      <i></i>
      <strong> {{ dashboard.realtime === null ? '—' : dashboard.number(dashboard.realtime) }} </strong>
      visitors now
    </span>
    <Btn
      class="button"
      variant="outline"
      icon="tabler:download"
      :disabled="dashboard.loading || !dashboard.exportRows.length"
      @click="dashboard.exportCsv"
      r-if="dashboard.kind !== 'benchmark'"
    >
      Export
    </Btn>
  </div>
</div>`

const reportFeedback = html`<div>
  <FormStatus
    class="report-status error"
    tone="danger"
    role="alert"
    r-if="dashboard.error"
  >
    {{ dashboard.error }}
  </FormStatus>
  <FormStatus
    class="report-status"
    tone="warning"
    r-for="warning in dashboard.warnings"
  >
    {{ warning }}
  </FormStatus>
</div>`

const reportFooter = html`<footer class="page-footer">
  <span>
    <span class="footer-dot"></span>
    Private by design. No cookies required.
  </span>
  <span> {{ dashboard.updatedAt ? 'Updated at ' + dashboard.updatedAt : 'ZoneTree Analytics' }} · {{
      dashboard.config.timeZone }} </span>
</footer>`

const emptyReport = html`<div class="empty-state">
  <h3>{{ title || 'No data for this selection' }}</h3>
  <p>{{ description || 'Try a different date range or remove a filter.' }}</p>
</div>`

const reportTable = html`<div>
  <div class="table-wrap" r-if="data.rows.length">
    <table class="data-table">
      <thead>
        <tr>
          <th
            scope="col"
            r-for="(header,index) in data.headers"
            :class="{ numeric:index > 0 }"
          >
            {{ header }}
          </th>
        </tr>
      </thead>
      <tbody>
        <tr r-for="row in data.rows">
          <td r-for="cell in row.cells" :class="{numeric:cell.numeric}">
            <button
              type="button"
              r-if="cell.dimension"
              @click="onFilter(cell.dimension,cell.value)"
              r-text="cell.text"
            ></button>
            <span r-else r-text="cell.text"></span>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
  <EmptyReport r-else/>
</div>`

const pagination = html`<div class="pagination">
  <span> {{ dashboard.totalRows ? dashboard.number(dashboard.offset + 1) + '–' +
      dashboard.number(dashboard.offset + dashboard.table.rows.length) + ' of ' +
      dashboard.number(dashboard.totalRows) : '0 rows' }} · export includes displayed rows </span>
  <div>
    <Btn
      class="button"
      variant="outline"
      :disabled="dashboard.offset === 0 || dashboard.loading"
      @click="dashboard.page(-1)"
    >
      ← Previous
    </Btn>
    <Btn
      class="button"
      variant="outline"
      :disabled="dashboard.offset + dashboard.table.rows.length >= dashboard.totalRows || dashboard.loading"
      @click="dashboard.page(1)"
    >
      Next →
    </Btn>
  </div>
</div>`

const breakdown = html`<Panel>
  <div class="panel-heading">
    <div>
      <h2>{{ title }}</h2>
      <p>{{ subtitle }}</p>
    </div>
    <a class="panel-link" :href="href">Details ↗</a>
  </div>
  <div class="breakdown-list" r-if="rows.length">
    <div class="list-heading">
      <span>{{ column || 'NAME' }}</span>
      <span>VISITORS</span>
    </div>
    <button
      class="breakdown-row"
      type="button"
      r-for="row in rows"
      :disabled="row.value === null"
      :style="{ '--bar-width': Math.min(100,Math.max(0,row.visitors / max * 80)) + '%' }"
      @click="onFilter(dimension,row.value)"
    >
      <span class="row-name">
        <span class="initial">{{ (row.value || '?').slice(0,1).toUpperCase() }}</span>
        {{ dimension === 'country' ? countryName(row.value) : row.value || '(empty)' }}
      </span>
      <span class="row-count">
        {{ number(row.visitors) }}
        <small>{{ percent(row.visitorShare) }}</small>
      </span>
    </button>
  </div>
  <EmptyReport r-else/>
</Panel>`

export function defineSharedReportComponents() {
  return {
    ReportHeader: defineComponent<ReportHeader>(reportHeader, {
      props: ['dashboard'],
    }),
    ReportControls: defineReportControlsComponent(),
    ReportFeedback: defineComponent<ReportFeedback>(reportFeedback, {
      props: ['dashboard'],
    }),
    ReportFooter: defineComponent<ReportFooter>(reportFooter, {
      props: ['dashboard'],
    }),
    EmptyReport: defineComponent<EmptyReport>(emptyReport, {
      props: ['title', 'description'],
    }),
    ReportTable: defineComponent<ReportTable>(reportTable, {
      props: ['data', 'onFilter'],
    }),
    ReportPagination: defineComponent<ReportPagination>(pagination, {
      props: ['dashboard'],
    }),
    ReportBreakdown: defineComponent<ReportBreakdown>(breakdown, {
      props: [
        'title',
        'subtitle',
        'href',
        'column',
        'rows',
        'dimension',
        'onFilter',
        'countryName',
        'number',
        'percent',
      ],
      context: (head) => ({
        ...head.props,
        max: computed(() =>
          Math.max(
            1,
            ...((unref(head.props.rows) || []) as Breakdown[]).map(
              (row) => row.visitors,
            ),
          ),
        ),
      }),
    }),
  }
}
