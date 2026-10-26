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
import type { SampleBenchmarkStatus, SampleDataJobs } from './jobs'

export interface SampleBenchmarkPanel {
  dashboard: DashboardModel | SRef<DashboardModel>
  jobs: SampleDataJobs | SRef<SampleDataJobs>
  count: SRef<number | string>
  days: SRef<number | string>
  workers: SRef<number | string>
  percentiles: SRef<boolean>
  indexes: SRef<boolean>
  geolocation: SRef<boolean>
  cities: SRef<boolean>
  benchmark: ComputedRef<SampleBenchmarkStatus | null>
  run: (event: Event) => Promise<void>
  seconds: (milliseconds: number) => string
  viewUrl: ComputedRef<string>
}

const template = html`<Panel class="form-panel sample-benchmark-panel">
  <h2>Benchmark ingestion</h2>
  <p>
    Measure ingestion of the same realistic traffic in a fresh dataset. Events
    are prepared in memory before timing starts. The latest successful run is
    kept for browsing.
  </p>
  <AppForm class="sample-data-form" @submit="run">
    <div class="form-grid">
      <FormInputField
        id="benchmark-event-count"
        label="Events"
        type="number"
        min="1"
        max="10000000"
        step="1"
        :model="count"
        :required="true"
        :disabled="jobs.busy"/>
      <FormInputField
        id="benchmark-days"
        label="Days"
        type="number"
        min="1"
        max="90"
        step="1"
        :model="days"
        :required="true"
        :disabled="jobs.busy"/>
      <FormInputField
        id="benchmark-workers"
        label="Workers"
        type="number"
        min="1"
        max="64"
        step="1"
        :model="workers"
        :required="true"
        :disabled="jobs.busy"/>
    </div>
    <FormCheck
      id="benchmark-percentiles"
      label="Include load-time percentile aggregation"
      :checked="percentiles"
      :disabled="jobs.busy"/>
    <FormCheck
      id="benchmark-indexes"
      label="Maintain dimension and rollup indexes"
      :checked="indexes"
      :disabled="jobs.busy"/>
    <FormCheck
      id="benchmark-geolocation"
      label="Resolve visitor countries from IP addresses"
      :checked="geolocation"
      :disabled="jobs.busy || !(benchmark && benchmark.geolocationAvailable)"/>
    <p class="field-hint" r-if="benchmark && !benchmark.geolocationAvailable">
      Needs an IP location database: choose one in the Location settings and
      wait for its download.
    </p>
    <FormCheck
      id="benchmark-cities"
      label="Also resolve regions and cities"
      :checked="cities"
      :disabled="jobs.busy || !geolocation || !(benchmark && benchmark.citiesAvailable)"/>
    <p
      class="field-hint"
      r-if="benchmark && benchmark.geolocationAvailable && !benchmark.citiesAvailable"
    >
      Needs a database with cities: choose DB-IP Lite in the Location settings.
    </p>
    <div
      class="sample-job-status"
      r-if="benchmark && benchmark.state !== 'idle'"
      aria-live="polite"
    >
      <div r-if="benchmark.state === 'preparing'">
        <progress
          class="sample-progress"
          :value="benchmark.prepared"
          :max="benchmark.requested"
          aria-label="Benchmark preparation progress"
        ></progress>
        <p>
          Preparing {{ dashboard.number(benchmark.prepared) }} / {{ dashboard.number(benchmark.requested) }}
          events
        </p>
      </div>
      <div
        r-if="benchmark.state === 'running' || benchmark.state === 'ingesting' || benchmark.state === 'flushing' || benchmark.state === 'cancelling'"
      >
        <progress
          class="sample-progress"
          :value="benchmark.ingested"
          :max="benchmark.requested"
          aria-label="Benchmark ingestion progress"
        ></progress>
        <p>
          {{ dashboard.number(benchmark.ingested) }} / {{ dashboard.number(benchmark.requested) }} ingested ·
          {{ dashboard.number(benchmark.submitted) }} submitted · {{ seconds(benchmark.elapsedMs) }} s
        </p>
        <dl class="sample-ingestion-metrics">
          <div>
            <dt>Ingested</dt>
            <dd>{{ dashboard.number(benchmark.ingested) }}</dd>
          </div>
          <div>
            <dt>Queued</dt>
            <dd>{{ dashboard.number(benchmark.submitted - benchmark.ingested) }}</dd>
          </div>
          <div>
            <dt>Average so far</dt>
            <dd>{{ dashboard.number(benchmark.eventsPerSecond) }} <small>events/s</small></dd>
          </div>
        </dl>
      </div>
      <p
        r-if="benchmark.state === 'flushing' || benchmark.state === 'cancelling'"
      >
        <progress
          class="sample-progress"
          aria-label="Finishing benchmark"
        ></progress>
        {{ benchmark.state === 'cancelling' ? 'Cancelling benchmark…' : 'Saving aggregates…' }}
      </p>
      <p r-if="benchmark.state === 'publishing'">
        Making benchmark reports available…
      </p>
      <p r-if="benchmark.state === 'compacting'">
        <progress
          class="sample-progress"
          aria-label="Preparing benchmark reports"
        ></progress>
        Preparing benchmark reports…
      </p>
      <p r-if="benchmark.state === 'completed'">Benchmark completed</p>
      <p r-if="benchmark.state === 'cancelled'">Benchmark cancelled</p>
    </div>
    <FormStatus tone="danger" r-if="benchmark && benchmark.error"
      >{{ benchmark.error }}</FormStatus>
    <div class="form-actions">
      <Btn
        class="button primary"
        tone="accent"
        type="submit"
        :disabled="jobs.busy || !jobs.status"
        >Run benchmark</Btn>
      <Btn
        class="button"
        variant="outline"
        @click="jobs.perform('cancel')"
        :disabled="jobs.pending"
        r-if="jobs.status && jobs.status.activeOperation === 'benchmark'"
        >Cancel benchmark</Btn>
    </div>
    <p
      role="status"
      r-if="jobs.status && jobs.status.activeOperation === 'benchmark-deletion'"
    >
      Deleting benchmark data…
    </p>
  </AppForm>
  <section
    class="sample-benchmark-result"
    r-if="benchmark && benchmark.result"
    aria-label="Last completed benchmark"
  >
    <h3>Last completed benchmark</h3>
    <p>
      {{ dashboard.number(benchmark.result.events) }} events · {{ benchmark.result.days }} days ·
      {{ benchmark.result.workers }} workers · Percentiles
      {{ benchmark.result.percentiles ? 'on' : 'off' }} · Indexes {{ benchmark.result.indexes ? 'on' : 'off' }} ·
      Geolocation {{ benchmark.result.geolocation ? (benchmark.result.cities ? 'cities' : 'countries') : 'off' }}
    </p>
    <dl class="sample-ingestion-metrics">
      <div>
        <dt>Ingestion throughput</dt>
        <dd>{{ dashboard.number(benchmark.result.eventsPerSecond) }} <small>events/s</small></dd>
      </div>
      <div>
        <dt>Ingestion + flush</dt>
        <dd>{{ seconds(benchmark.result.ingestionMs) }} <small>s</small></dd>
      </div>
      <div>
        <dt>Submission</dt>
        <dd>{{ seconds(benchmark.result.submissionMs) }} <small>s</small></dd>
      </div>
      <div>
        <dt>Preparation</dt>
        <dd>{{ seconds(benchmark.result.preparationMs) }} <small>s</small></dd>
      </div>
    </dl>
    <p class="field-hint">
      Throughput includes submission and the final flush. Preparation and
      cleanup are excluded.
    </p>
    <p class="field-hint">
      {{ benchmark.result.build }} · {{ benchmark.result.garbageCollection }} GC · Tiered
      compilation: {{ benchmark.result.tieredCompilation }} · {{ dashboard.number(benchmark.result.ingested) }}
      ingested · {{ benchmark.result.errors }} errors ·
      {{ benchmark.result.dropped }} dropped · {{ benchmark.result.rejected }} rejected
    </p>
    <div class="form-actions">
      <a class="button" :href="viewUrl">View benchmark reports</a>
      <Btn
        class="button danger"
        tone="danger"
        variant="outline"
        @click="jobs.perform('clear-benchmark')"
        :disabled="jobs.busy"
        >Delete benchmark data</Btn>
    </div>
  </section>
</Panel>`

export function defineSampleBenchmarkPanel() {
  return defineComponent<SampleBenchmarkPanel>(template, {
    props: ['dashboard', 'jobs'],
    context: (head) => {
      const jobs = unref(head.props.jobs)
      const count = sref<number | string>(5_000_000)
      const days = sref<number | string>(365)
      const workers = sref<number | string>(20)
      const percentiles = sref(false)
      const indexes = sref(true)
      const geolocation = sref(false)
      const cities = sref(false)
      return {
        ...head.props,
        count,
        days,
        workers,
        percentiles,
        indexes,
        geolocation,
        cities,
        benchmark: computed(() => jobs.status()?.benchmark ?? null),
        viewUrl: computed(() => {
          const model = unref(head.props.dashboard)
          const result = jobs.status()?.benchmark.result
          const query = new URLSearchParams({
            site: model.config().sampleData?.benchmark?.siteId ?? '',
            range: 'custom',
            from: result?.fromDate ?? '',
            to: result?.toDate ?? '',
            compare: 'none',
          })
          return `${model.assetPath('')}?${query}`
        }),
        seconds: (milliseconds) => (milliseconds / 1000).toFixed(3),
        run: async (event) => {
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
            jobs.error(
              'Choose 1–10,000,000 events, 1–365 days, and 1–64 workers.',
            )
            return
          }
          await jobs.perform('benchmark', {
            events,
            days: span,
            workers: concurrency,
            percentiles: percentiles(),
            indexes: indexes(),
            geolocation: geolocation(),
            cities: geolocation() && cities(),
          })
        },
      }
    },
  })
}
