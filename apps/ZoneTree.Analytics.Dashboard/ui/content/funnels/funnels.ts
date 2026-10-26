import { computed, defineComponent, html, observe, sref } from 'regor'
import type { FunnelResult } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'

export function createFunnelModel(model: DashboardModel) {
  let nextId = 0
  const step = (name: string, event: string, filter = '') => ({
    id: nextId++,
    name: sref(name),
    event: sref(event),
    filter: sref(filter),
  })
  const steps = sref([
    step('Visit website', 'pageview'),
    step('Sign up', 'signup'),
    step('Purchase', 'purchase'),
  ])
  const windowMinutes = sref<string | number>(30)
  const result = sref<FunnelResult | null>(null)
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
    steps,
    windowMinutes,
    result,
    width: (visitors: number) =>
      `${Math.max(0, Math.min(100, (visitors / (result()?.steps[0]?.visitors || 1)) * 100))}%`,
    addStep: () => {
      if (steps().length < 64) {
        steps([...steps(), step('New step', '')])
        result(null)
        model.exportRows([])
      }
    },
    removeStep: (id: number) => {
      if (steps().length > 2) {
        steps(steps().filter((value) => value.id !== id))
        result(null)
        model.exportRows([])
      }
    },
    run: async (event: Event) => {
      event.preventDefault()
      model.exportRows([])
      const values = steps().map((item) => ({
        name: item.name().trim(),
        event: item.event().trim(),
        filters: item.filter().trim() ? [item.filter().trim()] : [],
      }))
      const window = Number(windowMinutes())
      if (
        values.length < 2 ||
        values.some((item) => !item.name || !item.event) ||
        !Number.isFinite(window) ||
        window <= 0
      ) {
        model.error(
          'Enter names and events for at least two steps, and a positive conversion window.',
        )
        return
      }
      const response = await model.execute<FunnelResult>('funnel', {
        windowMinutes: window,
        steps: values,
      })
      if (response) {
        result(response)
        model.exportRows([
          [
            'Step',
            'Visitors',
            'Overall conversion',
            'Step conversion',
            'Drop-off',
          ],
          ...response.steps.map((item) => [
            item.name,
            item.visitors,
            item.conversionRate,
            item.stepConversionRate,
            item.dropOff,
          ]),
        ])
      }
    },
  }
}

export interface FunnelsPage {
  dashboard: DashboardModel
  funnel: ReturnType<typeof createFunnelModel>
}

const funnelsTemplate = html`<ReportHeader :dashboard="dashboard"/>
<ReportControls :dashboard="dashboard"/>
<ReportFeedback :dashboard="dashboard"/>
<Panel class="form-panel">
  <h2>Build a conversion funnel</h2>
  <p>
    Visitors must complete these steps in order, inside the conversion window.
    Funnels use raw events. Choose a range within your raw-event retention.
  </p>
  <AppForm class="funnel-form" @submit.prevent="funnel.run">
    <div class="funnel-step" r-for="(step,index) in funnel.steps">
      <span class="step-number">{{ index + 1 }}</span>
      <FormInputField
        :id="'step-name-' + step.id"
        label="Step name"
        :model="step.name"
        :required="true"
        :disabled="dashboard.loading"/>
      <FormInputField
        :id="'step-event-' + step.id"
        label="Event name"
        :model="step.event"
        :required="true"
        :disabled="dashboard.loading"/>
      <FormInputField
        :id="'step-filter-' + step.id"
        label="Filter (optional)"
        :model="step.filter"
        placeholder="page==/pricing"
        :disabled="dashboard.loading"/>
      <Btn
        class="icon-button"
        variant="none"
        icon="tabler:x"
        :iconOnly="true"
        :ariaLabel="'Remove step ' + (index + 1)"
        :disabled="funnel.steps.length <= 2 || dashboard.loading"
        @click="funnel.removeStep(step.id)"/>
    </div>
    <div class="form-actions">
      <Btn
        class="button"
        variant="outline"
        :disabled="funnel.steps.length >= 64 || dashboard.loading"
        @click="funnel.addStep"
      >
        + Add step
      </Btn>
      <FormInputField
        id="funnel-window"
        label="Conversion window (minutes)"
        type="number"
        min="1"
        :model="funnel.windowMinutes"
        :required="true"
        :disabled="dashboard.loading"/>
      <Btn
        class="button primary"
        tone="accent"
        type="submit"
        :disabled="dashboard.loading"
      >
        Run funnel
      </Btn>
    </div>
  </AppForm>
</Panel>
<Panel :aria-busy="dashboard.loading" r-if="funnel.result">
  <div class="panel-heading">
    <div>
      <h2>Conversion journey</h2>
      <p>{{ dashboard.percent(funnel.result.conversionRate) }} overall conversion</p>
    </div>
  </div>
  <div class="funnel-results">
    <div class="funnel-result" r-for="(step,index) in funnel.result.steps">
      <div class="funnel-result-header">
        <span>{{ index + 1 }}. {{ step.name }}</span>
        <strong>
          {{ dashboard.number(step.visitors) }} visitors · {{
            dashboard.percent(step.conversionRate) }}
        </strong>
      </div>
      <div class="funnel-bar-track">
        <div
          class="funnel-bar"
          :style="{'--bar-width':funnel.width(step.visitors)}"
        ></div>
      </div>
      <p class="field-hint">{{ index ? dashboard.number(step.dropOff) + ' visitors dropped off · ' +
          dashboard.percent(step.stepConversionRate) + ' converted from the previous step' :
          'Visitors who completed the first step' }}</p>
    </div>
  </div>
</Panel>
<ReportFooter :dashboard="dashboard"/>`

export function defineFunnelsPage(model: DashboardModel) {
  return defineComponent<FunnelsPage>(funnelsTemplate, {
    context: () => ({ dashboard: model, funnel: createFunnelModel(model) }),
  })
}

void mountReport({
  kind: 'funnels',
  selector: '#funnels-app',
  component: defineFunnelsPage,
})
