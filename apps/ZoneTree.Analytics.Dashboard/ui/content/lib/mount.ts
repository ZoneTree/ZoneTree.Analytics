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
import { type Component, createApp, html } from 'regor'
import { defineSharedReportComponents } from '../components/report-components'
import { assetUrl, loadConfig } from './config'
import { defaultConfig } from './contracts'
import {
  createDashboardModel,
  type DashboardModel,
  type Loader,
} from './dashboard-model'
import type { ReportKind } from './reports'

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
  let icons: Record<string, string> = {}
  try {
    config = await loadConfig()
    const response = await fetch(assetUrl('assets/dashboard-icons.json'))
    if (!response.ok)
      throw new Error(
        'Cannot load dashboard icons. Rebuild the PureStack site.',
      )
    icons = (await response.json()) as Record<string, string>
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
    template: html`
      <DashboardPage />
    `,
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
