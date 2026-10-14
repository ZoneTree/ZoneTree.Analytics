import { defineComponent, html } from 'regor'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'
import { defineSampleBenchmarkPanel } from './benchmark'
import { defineSampleDataPanel } from './controls'
import { createSampleDataJobs, type SampleDataJobs } from './jobs'

export interface SampleDataPage {
  dashboard: DashboardModel
  jobs: SampleDataJobs
}

const template = html`
  <ReportHeader :dashboard="dashboard" />
  <ReportFeedback :dashboard="dashboard" />
  <FormStatus tone="danger" r-if="jobs.error">{{ jobs.error }}</FormStatus>
  <SampleDataPanel :dashboard="dashboard" :jobs="jobs"
    r-if="dashboard.config.mode === 'live' && dashboard.config.sampleData" />
  <SampleBenchmarkPanel :dashboard="dashboard" :jobs="jobs"
    r-if="dashboard.config.mode === 'live' && dashboard.config.sampleData" />
  <Panel class="form-panel"
    r-if="dashboard.config.mode !== 'live' || !dashboard.config.sampleData">
    <h2>Sample data controls are unavailable</h2>
    <p>Use the sample host in Development mode with a live API connection to generate and delete performance sample data.</p>
    <a class="button" :href="dashboard.assetPath('connection/')">Open connection settings</a>
  </Panel>
  <ReportFooter :dashboard="dashboard" />
`

export function defineSampleDataPage(model: DashboardModel) {
  return defineComponent<SampleDataPage>(template, {
    context: () => ({ dashboard: model, jobs: createSampleDataJobs(model) }),
  })
}

void mountReport({
  kind: 'sample-data',
  selector: '#sample-data-app',
  component: defineSampleDataPage,
  components: {
    SampleDataPanel: defineSampleDataPanel(),
    SampleBenchmarkPanel: defineSampleBenchmarkPanel(),
  },
})
