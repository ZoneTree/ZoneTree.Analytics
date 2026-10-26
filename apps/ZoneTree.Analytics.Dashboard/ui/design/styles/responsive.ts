import { sidebarColors as sidebar } from '../colors'
import type { DashboardStyleContext } from './context'

export function registerResponsiveStyles(context: DashboardStyleContext) {
  const { root, accent, colors } = context
  const wide = root.media('min-width: 1500px')
  wide.select('.chart-wrap').height('310px')

  const tablet = root.media('max-width: 1150px')
  tablet.select('.app-shell').gridTemplateColumns('205px minmax(0,1fr)')

  tablet.select('#main').padding('28px 24px')

  tablet.select('.topbar-content').padding('0 24px')

  tablet.select('.metric-grid').gap('8px')

  tablet.select('.metric-card').padding('16px 12px')

  tablet.select('.metric-value').fontSize('23px')

  tablet.select('.metric-change > span:last-child').display('none')

  tablet.select('.heading-actions').gap('8px')

  tablet.select('.toolbar').flexWrap('wrap')

  const drawer = root.media('max-width: 850px')
  drawer.select('.app-shell').gridTemplateColumns('minmax(0,1fr)')

  drawer
    .select('.sidebar')
    .position('fixed')
    .top('0')
    .height('100dvh')
    .zIndex('50')
    .visibility('hidden')
    .left('0')
    .width('232px')
    .transform('translateX(-100%)')
    .transition('transform 180ms')
    .boxShadow('16px 0 60px #00000033')

  drawer
    .select('.nav-toggle:checked ~ .app-shell .nav-backdrop')
    .display('block')
    .position('fixed')
    .inset('0')
    .zIndex('45')
    .background(colors.overlay)
    .cursor('pointer')

  drawer
    .select('.nav-toggle:checked ~ .app-shell .sidebar')
    .transform('translateX(0)')
    .visibility('visible')

  drawer.select('.nav-toggle').display('block')

  drawer.select('.mobile-only').display('inline-flex')

  // A quiet control on the dark drawer, not the light bordered icon button of the page.
  drawer
    .select('.sidebar .drawer-close')
    .position('absolute')
    .top('12px')
    .right('12px')
    .width('30px')
    .minHeight('30px')
    .height('30px')
    .padding('0')
    .border('0')
    .borderRadius('6px')
    .background('transparent')
    .color(sidebar.muted)
    .cursor('pointer')

  drawer
    .select('.sidebar .drawer-close:hover')
    .background(sidebar.hover)
    .color(sidebar.ink)

  drawer.select('.sidebar .drawer-close .icon').width('16px').height('16px')

  drawer
    .select('.nav-toggle:focus-visible ~ .app-shell .mobile-only')
    .outline(`2px solid ${accent}`)
    .outlineOffset('4px')

  drawer.select('.topbar').height('63px')

  drawer.select('.noscript-message').left('20px')

  const mobile = root.media('max-width: 600px')
  mobile.select('#main').padding('25px 16px')

  mobile.select('.topbar-content').padding('0 16px')

  mobile.select('.breadcrumb').gap('9px')

  mobile.select('.topbar-actions').gap('4px')

  mobile
    .select('.page-heading')
    .alignItems('start')
    .flexWrap('wrap')
    .gap('18px')

  mobile
    .select('.heading-actions')
    .width('100%')
    .justifyContent('space-between')
    .gap('10px')

  mobile.select('.page-heading h1').fontSize('27px')

  mobile.select('.toolbar-right').width('100%').gap('5px')

  mobile.select('.control').padding('0 7px')

  mobile
    .select('.metric-grid')
    .gridTemplateColumns('repeat(2,minmax(0,1fr))')
    .gap('10px')

  mobile.select('.metric-card:last-child').gridColumn('1/-1')

  mobile
    .select(
      '.overview-grid, .audience-grid, .form-grid, .sample-data-form .form-grid',
    )
    .gridTemplateColumns('minmax(0,1fr)')

  mobile
    .select('.sample-ingestion-metrics')
    .gridTemplateColumns('repeat(2,minmax(0,1fr))')

  mobile.select('.chart-wrap').height('210px')

  mobile.select('.panel-heading').padding('18px 16px')

  mobile.select('.breakdown-list').padding('0 16px 15px')

  mobile.select('.page-footer').alignItems('start').flexDirection('column')

  mobile.select('.funnel-step').gridTemplateColumns('25px minmax(0,1fr) 34px')

  mobile.select('.funnel-step label').gridColumn('2')

  mobile.select('.funnel-step .step-number').gridRow('1/4')

  mobile.select('.funnel-step button').gridColumn('3').gridRow('1/4')

  mobile.select('.form-panel').padding('18px')

  mobile.select('.device-layout').gap('15px')

  const narrow = root.media('max-width: 400px')
  narrow.select('.breadcrumb').minWidth('0')

  narrow.select('.breadcrumb > span').display('none')

  narrow
    .select('.breadcrumb strong')
    .minWidth('0')
    .overflow('hidden')
    .textOverflow('ellipsis')
    .whiteSpace('nowrap')

  const reducedMotion = root.media('prefers-reduced-motion: reduce')
  reducedMotion
    .select('.zt-dashboard *')
    .transition('none !important')
    .animation('none !important')
    .scrollBehavior('auto !important')
}
