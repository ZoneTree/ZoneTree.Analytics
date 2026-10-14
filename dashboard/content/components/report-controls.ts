import type { FormSelectOption } from '@purestack/ts-components'
import { defineComponent, html, type RefOrValue, type SRef, sref } from 'regor'
import type { DashboardScope } from '../lib/dashboard/scope'
import type { DashboardStatus } from '../lib/dashboard/state'
import type { DashboardModel } from '../lib/dashboard-model'
import { filterExpression } from '../lib/format'

export interface DashboardFilters {
  filterOpen: SRef<boolean>
  filterDimension: SRef<string>
  filterOperator: SRef<string>
  filterValue: SRef<string>
  filterError: SRef<string>
  addFilter: (dimension: string, value: string | null) => void
  removeFilter: (index: number) => void
  applyFilter: (event: Event) => void
  openFilter: () => void
  closeFilter: () => void
}

export function createDashboardFilters(scope: DashboardScope, status: DashboardStatus): DashboardFilters {
  const filterOpen = sref(false)
  const filterDimension = sref('source')
  const filterOperator = sref('==')
  const filterValue = sref('')
  const filterError = sref('')
  const filterDialog = () => document.getElementById('report-filter-dialog') as HTMLDialogElement | null
  const insertFilter = (expression: string) => {
    if (!scope.filters().includes(expression)) scope.filters([...scope.filters(), expression])
  }
  return {
    filterOpen, filterDimension, filterOperator, filterValue, filterError,
    addFilter: (dimension, value) => {
      if (value == null) return
      try { insertFilter(filterExpression(dimension, '==', value)) }
      catch (failure) { status.error((failure as Error).message) }
    },
    removeFilter: (index) => scope.filters(scope.filters().filter((_, current) => current !== index)),
    applyFilter: (event) => {
      event.preventDefault()
      try {
        insertFilter(filterExpression(filterDimension(), filterOperator(), filterValue()))
        filterOpen(false)
        filterDialog()?.close()
        filterValue('')
        filterError('')
      } catch (failure) { filterError((failure as Error).message) }
    },
    openFilter: () => { filterError(''); filterOpen(true); filterDialog()?.showModal() },
    closeFilter: () => { filterOpen(false); filterDialog()?.close() },
  }
}
const reportControls = html`<div>
  <section class="toolbar" aria-label="Report controls">
    <div class="toolbar-left">
      <div class="control">
        <Icon name="tabler:world"/>
        <FormSelectField
          id="site-select"
          aria-label="Site"
          :model="dashboard.site"
          :options="dashboard.siteOptions"
          variant="none"/>
      </div>
      <Btn
        class="button quiet"
        variant="none"
        icon="tabler:adjustments-horizontal"
        @click="dashboard.openFilter"
      >
        Add filter
      </Btn>
    </div>
    <div class="toolbar-right">
      <div class="control">
        <Icon name="tabler:calendar"/>
        <FormSelectField
          id="range-select"
          aria-label="Date range"
          :model="dashboard.range"
          :options="rangeOptions"
          variant="none"/>
      </div>
      <div class="control compare-control">
        <FormSelectField
          id="compare-select"
          aria-label="Compare with"
          :model="dashboard.compare"
          :options="compareOptions"
          variant="none"
          r-if="dashboard.kind !== 'funnels'"/>
        <span class="field-hint" r-else>Ordered visitor journey</span>
      </div>
      <Btn
        class="icon-button"
        variant="outline"
        icon="tabler:refresh"
        :iconOnly="true"
        ariaLabel="Refresh reports"
        :disabled="dashboard.loading"
        @click="dashboard.load"
        r-if="dashboard.kind !== 'funnels' && dashboard.kind !== 'explorer'"/>
    </div>
  </section>
  <div class="custom-range" r-if="dashboard.range === 'custom'">
    <FormInputField
      id="date-from"
      label="From"
      type="date"
      :model="dashboard.dateFrom"
      :required="true"/>
    <FormInputField
      id="date-to"
      label="Through"
      type="date"
      :model="dashboard.dateTo"
      :required="true"/>
    <Btn class="button primary" tone="accent" @click="dashboard.applyDates"
      >Apply dates</Btn
    >
  </div>
  <div class="filter-chips" aria-label="Active filters">
    <Btn
      r-for="(filter,index) in dashboard.filters"
      class="filter-chip"
      variant="none"
      :ariaLabel="'Remove filter ' + filter"
      @click="dashboard.removeFilter(index)"
    >
      {{ filter }} ×
    </Btn>
  </div>
  <dialog
    id="report-filter-dialog"
    aria-labelledby="filter-title"
    @cancel="dashboard.closeFilter"
  >
    <form class="filter-form" @submit="dashboard.applyFilter">
      <div class="dialog-intro">
        <div class="dialog-heading">
          <h2 id="filter-title">Filter your report</h2>
          <Btn
            class="icon-button"
            variant="none"
            icon="tabler:x"
            :iconOnly="true"
            ariaLabel="Close filter dialog"
            @click="dashboard.closeFilter"/>
        </div>
        <p>Focus on a specific page, source, or audience.</p>
      </div>
      <FormSelectField
        aria-label="Dimension"
        label="Dimension"
        :model="dashboard.filterDimension"
        :options="dimensionOptions"/>
      <FormSelectField
        aria-label="Condition"
        label="Condition"
        :model="dashboard.filterOperator"
        :options="operatorOptions"/>
      <FormInputField
        label="Value"
        :model="dashboard.filterValue"
        placeholder="e.g. Google, DE, Mobile, /pricing"/>
      <p class="field-hint">
        Page filters make session metrics unavailable. Values use the engine's
        case-sensitive dimension names.
      </p>
      <FormStatus tone="danger" r-if="dashboard.filterError">
        {{ dashboard.filterError }}
      </FormStatus>
      <div class="dialog-actions">
        <Btn class="button" variant="outline" @click="dashboard.closeFilter"
          >Cancel</Btn
        >
        <Btn class="button primary" tone="accent" type="submit"
          >Apply filter</Btn
        >
      </div>
    </form>
  </dialog>
</div>`

export interface ReportControls {
  dashboard: RefOrValue<DashboardModel>
  rangeOptions: FormSelectOption[]
  compareOptions: FormSelectOption[]
  dimensionOptions: FormSelectOption[]
  operatorOptions: FormSelectOption[]
}
export function defineReportControlsComponent() {
  return defineComponent<ReportControls>(reportControls, {
    props: ['dashboard'],
    context: (head) => ({
      ...head.props,
      rangeOptions: [
        { value: 'today', label: 'Today' },
        { value: 'yesterday', label: 'Yesterday' },
        { value: '7d', label: 'Last 7 days' },
        { value: '30d', label: 'Last 30 days' },
        { value: 'this-month', label: 'This month' },
        { value: 'last-month', label: 'Last month' },
        { value: 'this-year', label: 'This year' },
        { value: 'custom', label: 'Custom range' },
      ],
      compareOptions: [
        { value: 'previous-period', label: 'vs. previous period' },
        { value: 'previous-year', label: 'vs. previous year' },
        { value: 'none', label: 'No comparison' },
      ],
      dimensionOptions: [
        'source',
        'country',
        'device',
        'browser',
        'os',
        'channel',
        'utm_campaign',
        'page',
        'hostname',
      ].map((value) => ({ value, label: value })),
      operatorOptions: [
        { value: '==', label: 'is' },
        { value: '!=', label: 'is not' },
        { value: '^=', label: 'starts with' },
        { value: '~=', label: 'contains' },
      ],
    }),
  })
}
