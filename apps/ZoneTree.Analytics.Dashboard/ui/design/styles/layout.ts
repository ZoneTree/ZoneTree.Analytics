import type { DashboardStyleContext } from './context'

export function registerLayoutStyles(context: DashboardStyleContext) {
  const { root, ink, muted, edge, surface, accent, colors } = context
  root
    .select('.app-shell')
    .display('grid')
    .gridTemplateColumns('232px minmax(0,1fr)')
    .minHeight('100vh')

  root.select('.main-shell').minWidth('0')

  root
    .select('.topbar')
    .height('76px')
    .borderBottom(`1px solid ${edge}`)
    .padding('0')
    .background(surface)

  root.select('.zt-dashboard .topbar').padding('0')

  root
    .select('.topbar-content')
    .width('100%')
    .maxWidth('1580px')
    .height('100%')
    .margin('0 auto')
    .padding('0 40px')
    .display('flex')
    .alignItems('center')
    .justifyContent('space-between')

  root
    .select('.breadcrumb, .topbar-actions')
    .display('flex')
    .alignItems('center')
    .gap('17px')

  root
    .select('.topbar-actions')
    .display('grid')
    .gridAutoFlow('column')
    .gridAutoColumns('2.625rem')
    .justifyItems('center')
    .flexShrink('0')
    .gap('4px')

  root.select('.breadcrumb').color(muted).fontSize('12px')

  root.select('.breadcrumb strong').color(ink).fontWeight('550')

  root.select('.slash').color(edge)

  root
    .select('.avatar')
    .width('31px')
    .height('31px')
    .background(colors.accentSoft)
    .color(accent)
    .border(`1px solid ${edge}`)
    .borderRadius('50%')
    .display('grid')
    .placeItems('center')
    .fontSize('10px')
    .fontWeight('650')

  root
    .select('#main')
    .maxWidth('1580px')
    .margin('0 auto')
    .padding('37px 40px 24px')

  root
    .select('.page-heading')
    .display('flex')
    .justifyContent('space-between')
    .alignItems('center')
    .gap('20px')
    .marginBottom('29px')

  root
    .select('.eyebrow')
    .color(muted)
    .fontSize('9px')
    .letterSpacing('1.6px')
    .marginBottom('7px')

  root
    .select('.page-heading h1')
    .fontSize('30px')
    .lineHeight('1.25')
    .letterSpacing('-1.1px')
    .fontWeight('600')

  root.select('.page-heading p').color(muted).fontSize('12px').marginTop('8px')

  root
    .select('.heading-actions')
    .display('flex')
    .alignItems('center')
    .gap('14px')
    .flexWrap('wrap')

  root
    .select('.live-count')
    .display('flex')
    .gap('5px')
    .alignItems('center')
    .fontSize('11px')
    .color(muted)
    .whiteSpace('nowrap')

  root.select('.live-count strong').color(ink).fontWeight('650')

  root
    .select('.live-count i, .footer-dot')
    .display('inline-block')
    .width('6px')
    .height('6px')
    .background(colors.success)
    .borderRadius('50%')
    .marginRight('5px')

  root
    .select('.nav-toggle')
    .display('none')
    .position('absolute')
    .width('1px')
    .height('1px')
    .opacity('0')
    .pointerEvents('none')

  root
    .select('.page-footer')
    .borderTop(`1px solid ${edge}`)
    .paddingTop('20px')
    .marginTop('30px')
    .display('flex')
    .justifyContent('space-between')
    .alignItems('center')
    .gap('12px')
    .color(muted)
    .fontSize('9px')

  root.select('.footer-dot').width('4px').height('4px')

  root
    .select('.empty-state')
    .padding('48px 25px')
    .textAlign('center')
    .color(muted)
    .fontSize('12px')

  root.select('.empty-state h3').fontSize('16px').color(ink).marginBottom('8px')

  root
    .select('.noscript-message')
    .position('fixed')
    .bottom('20px')
    .left('250px')
    .right('20px')
    .padding('20px')
    .background(surface)
    .border(`1px solid ${edge}`)
    .zIndex('50')
}
