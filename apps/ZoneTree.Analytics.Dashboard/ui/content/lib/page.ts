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
  tabler_alert_triangle,
  tabler_arrow_left,
  tabler_arrow_right,
  tabler_check,
  tabler_copy,
  tabler_key,
  tabler_lock,
  tabler_logout,
  tabler_pencil,
  tabler_plus,
  tabler_refresh,
  tabler_trash,
  tabler_user_plus,
  tabler_x,
} from '@purestack/ts-svg-icons'
import { type Component, createApp, html } from 'regor'

const icons: Readonly<Record<string, string>> = {
  'tabler:alert-triangle': tabler_alert_triangle,
  'tabler:arrow-left': tabler_arrow_left,
  'tabler:arrow-right': tabler_arrow_right,
  'tabler:check': tabler_check,
  'tabler:copy': tabler_copy,
  'tabler:key': tabler_key,
  'tabler:lock': tabler_lock,
  'tabler:logout': tabler_logout,
  'tabler:pencil': tabler_pencil,
  'tabler:plus': tabler_plus,
  'tabler:refresh': tabler_refresh,
  'tabler:trash': tabler_trash,
  'tabler:user-plus': tabler_user_plus,
  'tabler:x': tabler_x,
}

export interface PageOptions<TContext extends object> {
  selector: string
  component: Component<TContext>
  components?: Record<string, Component<object>>
}

// Mounts an account, setup or admin page: PureStack form components without a report model.
export function mountPage<TContext extends object>(
  options: PageOptions<TContext>,
) {
  const app = createApp(
    {
      components: {
        ...defineBadgeComponents(),
        ...defineButtonComponents(),
        ...defineFlexComponents(),
        ...defineFormComponents(),
        ...defineFormInputField(),
        ...defineFormSelectField(),
        ...defineIconComponents((name) => icons[name] || ''),
        ...definePanelComponents(),
        ...options.components,
        DashboardPage: options.component,
      },
    },
    {
      selector: options.selector,
      template: html`
        <DashboardPage />
      `,
    },
  )
  window.addEventListener(
    'pagehide',
    (event) => {
      if (!(event as PageTransitionEvent).persisted) app.unbind()
    },
    { once: true },
  )
  return app
}

export const roleDescriptions: Record<string, string> = {
  Administrator: 'Manages settings, sites and users, and reads every site.',
  Analyst: 'Reads reports and runs ad-hoc queries on assigned sites.',
  Viewer: 'Reads reports on assigned sites.',
}

export function formatDate(value: string | null | undefined): string {
  if (!value) return '—'
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value))
}

export function describeAgent(userAgent: string | null | undefined): string {
  if (!userAgent) return 'Unknown browser'
  const browser = /Edg\//.test(userAgent)
    ? 'Edge'
    : /Firefox\//.test(userAgent)
      ? 'Firefox'
      : /Chrome\//.test(userAgent)
        ? 'Chrome'
        : /Safari\//.test(userAgent)
          ? 'Safari'
          : 'Browser'
  const system = /Windows/.test(userAgent)
    ? 'Windows'
    : /Android/.test(userAgent)
      ? 'Android'
      : /iPhone|iPad/.test(userAgent)
        ? 'iOS'
        : /Mac OS X/.test(userAgent)
          ? 'macOS'
          : /Linux/.test(userAgent)
            ? 'Linux'
            : 'unknown system'
  return `${browser} on ${system}`
}
