import { defineComponent, html } from 'regor'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'
import { type BenchmarkJobs, createBenchmarkJobs } from './jobs'
import { defineBenchmarkPanel } from './panel'

export interface BenchmarkPage {
  dashboard: DashboardModel
  jobs: BenchmarkJobs
}

const template = html`<ReportHeader :dashboard="dashboard"/>
<ReportFeedback :dashboard="dashboard"/>
<FormStatus tone="danger" r-if="jobs.error"
  >{{ jobs.error }}</FormStatus>
<BenchmarkPanel
  :dashboard="dashboard"
  :jobs="jobs"
  r-if="dashboard.config.mode === 'live' && dashboard.config.benchmark"/>
<Panel
  class="form-panel"
  r-if="dashboard.config.mode !== 'live' || !dashboard.config.benchmark"
>
  <h2>Benchmark controls are unavailable</h2>
  <p>
    Benchmarks need the dashboard server in Development mode, signed in as the
    administrator.
  </p>
</Panel>
<ReportFooter :dashboard="dashboard"/>`

export function defineBenchmarkPage(model: DashboardModel) {
  return defineComponent<BenchmarkPage>(template, {
    context: () => ({ dashboard: model, jobs: createBenchmarkJobs(model) }),
  })
}

void mountReport({
  kind: 'benchmark',
  selector: '#benchmark-app',
  component: defineBenchmarkPage,
  components: {
    BenchmarkPanel: defineBenchmarkPanel(),
  },
})
