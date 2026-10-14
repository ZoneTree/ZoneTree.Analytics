import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  type SRef,
  sref,
  unref,
} from 'regor'
import type { DashboardModel } from '../lib/dashboard-model'
import type { SampleDataJobs, SampleDataStatus } from './jobs'

export type { SampleDataStatus } from './jobs'

export interface SampleDataPanel {
  dashboard: DashboardModel | SRef<DashboardModel>
  jobs: SampleDataJobs | SRef<SampleDataJobs>
  count: SRef<number | string>
  days: SRef<number | string>
  workers: SRef<number | string>
  status: SRef<SampleDataStatus | null>
  error: SRef<string>
  pending: SRef<boolean>
  busy: ComputedRef<boolean>
  viewUrl: ComputedRef<string>
  progressLabel: ComputedRef<string>
  generate: (event: Event) => Promise<void>
  cancel: () => Promise<void>
  clear: () => Promise<void>
  preset: (count: number) => void
}

const template = html`<Panel class="form-panel sample-data-panel">
  <h2>Performance sample data</h2>
  <p>
    Generate real analytics events for the Performance samples site, then
    explore its reports. This dataset is separate from your normal traffic.
    Deleting it removes its events and rollups.
  </p>
  <AppForm class="sample-data-form" @submit="generate">
    <div class="form-grid">
      <FormInputField
        id="sample-event-count"
        label="Events"
        type="number"
        min="1"
        max="10000000"
        step="1"
        :model="count"
        :required="true"
        :disabled="busy"/>
      <FormInputField
        id="sample-day-count"
        label="Days"
        type="number"
        min="1"
        max="90"
        step="1"
        :model="days"
        :required="true"
        :disabled="busy"/>
      <FormInputField
        id="sample-workers"
        label="Workers"
        type="number"
        min="1"
        max="64"
        step="1"
        :model="workers"
        :required="true"
        :disabled="busy"/>
    </div>
    <div class="sample-presets" aria-label="Sample size presets">
      <Btn
        class="button"
        variant="outline"
        :disabled="busy"
        @click="preset(1000)"
        >1,000</Btn
      >
      <Btn
        class="button"
        variant="outline"
        :disabled="busy"
        @click="preset(100000)"
        >100,000</Btn
      >
      <Btn
        class="button"
        variant="outline"
        :disabled="busy"
        @click="preset(1000000)"
        >1 million</Btn
      >
      <Btn
        class="button"
        variant="outline"
        :disabled="busy"
        @click="preset(5000000)"
        >5 million</Btn
      >
    </div>
    <div class="sample-job-status" r-if="status" aria-live="polite">
      <p>{{ dashboard.number(status.totalGenerated) }} generated events in this dataset</p>
      <div r-if="status.requested > 0">
        <progress
          class="sample-progress"
          :value="status.ingested"
          :max="status.requested"
          aria-label="Sample generation progress"
        ></progress>
        <p>{{ progressLabel }}</p>
        <dl class="sample-ingestion-metrics">
          <div>
            <dt>Workers</dt>
            <dd>{{ dashboard.number(status.workers) }}</dd>
          </div>
          <div>
            <dt>Ingested</dt>
            <dd>{{ dashboard.number(status.ingested) }}</dd>
          </div>
          <div r-if="status.state === 'generating'">
            <dt>Live ingestion</dt>
            <dd>{{ dashboard.number(status.eventsPerSecond) }} <small>events/s</small></dd>
          </div>
          <div>
            <dt>Generation throughput</dt>
            <dd>{{ dashboard.number(status.averageEventsPerSecond) }} <small>events/s</small></dd>
          </div>
        </dl>
      </div>
      <p r-if="status.state === 'deleting'">Deleting sample data…</p>
    </div>
    <FormStatus tone="danger" r-if="status && status.error">
      {{ status.error }}
    </FormStatus>
    <div class="form-actions">
      <Btn
        class="button primary"
        tone="accent"
        type="submit"
        :disabled="busy || !status"
        >Generate sample data</Btn
      >
      <Btn
        class="button"
        variant="outline"
        @click="cancel"
        :disabled="pending"
        r-if="status && status.activeOperation === 'generation'"
        >Cancel generation</Btn
      >
      <Btn
        class="button danger"
        tone="danger"
        variant="outline"
        @click="clear"
        :disabled="busy || !status"
        >Delete sample data</Btn
      >
      <a class="button" :href="viewUrl">View sample reports</a>
    </div>
  </AppForm>
</Panel>`

export function defineSampleDataPanel() {
  return defineComponent<SampleDataPanel>(template, {
    props: ['dashboard', 'jobs'],
    context: (head) => {
      const dashboard = () => unref(head.props.dashboard)
      const count = sref<number | string>(10_000)
      const days = sref<number | string>(365)
      const workers = sref<number | string>(20)
      const { status, error, pending, busy, perform } = unref(head.props.jobs)
      return {
        ...head.props,
        count,
        days,
        workers,
        status,
        error,
        pending,
        busy,
        viewUrl: computed(
          () =>
            `${dashboard().assetPath('')}?site=${encodeURIComponent(dashboard().config().sampleData?.siteId ?? '')}`,
        ),
        progressLabel: computed(() => {
          const job = status()
          return job
            ? `${job.state}: ${dashboard().number(job.ingested)} / ${dashboard().number(job.requested)} ingested · ${dashboard().number(job.processed)} submitted · ${(job.elapsedMs / 1000).toFixed(1)} s`
            : ''
        }),
        preset: (value) => count(value),
        generate: async (event) => {
          event.preventDefault()
          const events = Number(count())
          const span = Number(days())
          const concurrency = Number(workers())
          if (
            !Number.isInteger(events) ||
            events < 1 ||
            events > 10_000_000 ||
            !Number.isInteger(span) ||
            span < 1 ||
            span > 365 ||
            !Number.isInteger(concurrency) ||
            concurrency < 1 ||
            concurrency > 64
          ) {
            error('Choose 1–10,000,000 events, 365 days, and 1–64 workers.')
            return
          }
          await perform('generate', {
            events,
            days: span,
            workers: concurrency,
          })
        },
        cancel: () => perform('cancel'),
        clear: () => perform('clear'),
      }
    },
  })
}
