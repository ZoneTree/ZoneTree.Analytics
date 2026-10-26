import {
  defineBadgeComponents,
  defineButtonComponents,
  defineFlexComponents,
  defineFormComponents,
  defineFormInputField,
  defineFormSelectField,
  defineIconComponents,
  definePanelComponents,
} from '@purestack/ts-components'
import {
  lucide_chevron_down,
  tabler_adjustments_horizontal,
  tabler_calendar,
  tabler_download,
  tabler_refresh,
  tabler_world,
  tabler_x,
} from '@purestack/ts-svg-icons'
import { type Component, createApp, html } from 'regor'
import { defineSharedReportComponents } from '../components/report-components'
import { loadConfig } from './config'
import { defaultConfig } from './contracts'
import {
  createDashboardModel,
  type DashboardModel,
  type Loader,
} from './dashboard-model'
import type { ReportKind } from './reports'

const icons: Readonly<Record<string, string>> = {
  'lucide:chevron-down': lucide_chevron_down,
  'tabler:adjustments-horizontal': tabler_adjustments_horizontal,
  'tabler:calendar': tabler_calendar,
  'tabler:download': tabler_download,
  'tabler:refresh': tabler_refresh,
  'tabler:world': tabler_world,
  'tabler:x': tabler_x,
}

// Every page owns its mount and lifecycle. No client router or shared app instance.
export interface PageMountOptions<TContext extends object> {
  kind: ReportKind
  selector: string
  component: (model: DashboardModel) => Component<TContext>
  loader?: Loader
  components?: Record<string, Component<object>>
}
export async function mountReport<TContext extends object>(
  options: PageMountOptions<TContext>,
) {
  let config = defaultConfig
  let configError = ''
  try {
    config = await loadConfig()
  } catch (error) {
    configError = (error as Error).message
  }
  const model = createDashboardModel(options.kind, config)
  const context = {
    components: {
      ...defineBadgeComponents(),
      ...defineButtonComponents(),
      ...defineFlexComponents(),
      ...defineFormComponents(),
      ...defineFormInputField(),
      ...defineFormSelectField(),
      ...defineIconComponents((name) => icons[name] || ''),
      ...definePanelComponents(),
      ...defineSharedReportComponents(),
      ...options.components,
      DashboardPage: options.component(model),
    },
  }
  const app = createApp(context, {
    selector: options.selector,
    template: html`<DashboardPage/>`,
  })
  model.start(configError ? null : options.loader || null)
  if (configError) model.error(configError)
  const cleanup = () => {
    model.dispose()
    app.unbind()
  }
  window.addEventListener(
    'pagehide',
    (event) => {
      if (!(event as PageTransitionEvent).persisted) cleanup()
    },
    { once: true },
  )
  window.addEventListener('pageshow', (event) => {
    if ((event as PageTransitionEvent).persisted && options.loader)
      void model.load()
  })
  return model
}
