import { styleBuilder, themes } from '@purestack/ts-style'
import { defineComponent, html, type RefOrValue } from 'regor'
import { sidebarColors } from './colors'

export interface DashboardLogo {
  href: RefOrValue<string>
  icon: RefOrValue<string>
  brandLetters: readonly string[]
  productLetters: readonly string[]
}

export function defineDashboardLogo() {
  return defineComponent<DashboardLogo>(
    html`<a class="dashboard-logo" :href="href" aria-label="ZoneTree Analytics home">
      <IconFrame class="dashboard-logo__mark" :name="icon" variant="none" aria-hidden="true"/>
      <span class="dashboard-logo__wordmark" aria-hidden="true">
        <span class="dashboard-logo__name"><span r-for="letter in brandLetters">{{ letter }}</span></span>
        <span class="dashboard-logo__caption"><span r-for="letter in productLetters">{{ letter }}</span></span>
      </span>
    </a>`,
    {
      props: ['href', 'icon'],
      context: (head) => ({
        ...head.props,
        brandLetters: Array.from('ZoneTree'),
        productLetters: Array.from('Analytics'),
      }),
    },
  )
}

export function registerDashboardLogoStyles() {
  themes.forEach((theme) => {
    const root = styleBuilder.get(theme)
    root.select('.dashboard-logo').css({
      display: 'inline-flex',
      alignItems: 'center',
      gap: '12px',
      maxWidth: '100%',
      color: sidebarColors.ink,
      borderRadius: '12px',
      lineHeight: '1',
    })
    root.select('.dashboard-logo .dashboard-logo__mark').css({
      width: '44px',
      height: '44px',
      padding: '0',
      borderRadius: '12px',
      color: sidebarColors.accent,
      background: sidebarColors.selection,
    })
    root.select('.dashboard-logo .dashboard-logo__mark .icon').css({
      width: '27px',
      height: '27px',
    })
    root.select('.dashboard-logo .dashboard-logo__mark .icon svg').css({
      width: '100%',
      height: '100%',
    })
    root.select('.dashboard-logo__wordmark').css({
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'flex-start',
      gap: '5px',
      minWidth: '0',
      fontFamily: "'Trebuchet MS', Arial, sans-serif",
      fontSynthesis: 'none',
      fontSize: '18px',
      fontWeight: '700',
    })
    root.select('.dashboard-logo__name, .dashboard-logo__caption').css({
      display: 'flex',
      justifyContent: 'space-between',
      width: '100px',
      color: sidebarColors.ink,
      whiteSpace: 'nowrap',
    })
  })
}
