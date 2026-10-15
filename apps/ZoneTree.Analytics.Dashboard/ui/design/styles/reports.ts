import type { DashboardStyleContext } from './context'

export function registerReportsStyles(context: DashboardStyleContext) {
  const { root, muted, edge, surface, accent, colors } = context
  root
    .select('.mode-badge, .api-response-badge')
    .padding('4px 9px')
    .borderRadius('5px')
    .fontSize('10px')
    .fontWeight('600')

  root.select('.report-badges').display('flex').alignItems('center').gap('8px')

  root.select('.api-response-badge').fontVariantNumeric('tabular-nums')

  root
    .select('.report-loading')
    .display('inline-flex')
    .alignItems('center')
    .gap('8px')
    .color(muted)
    .fontSize('11px')
    .whiteSpace('nowrap')

  root
    .select('.report-loading progress')
    .width('48px')
    .height('4px')
    .accentColor(accent)

  root
    .select('.report-status')
    .padding('12px 15px')
    .marginBottom('20px')
    .border(`1px solid ${edge}`)
    .background(colors.warningSoft)
    .color(colors.warning)
    .borderColor(colors.warningBorder)
    .borderRadius('8px')
    .fontSize('12px')

  root
    .select('.report-status.error')
    .background(colors.dangerSoft)
    .color(colors.danger)
    .borderColor(colors.dangerBorder)

  root
    .select('.metric-grid')
    .display('grid')
    .gridTemplateColumns('repeat(5,minmax(0,1fr))')
    .gap('14px')
    .marginBottom('23px')

  root
    .select('.metric-card')
    .border(`1px solid ${edge}`)
    .borderRadius('9px')
    .background(surface)
    .padding('18px 18px 16px')
    .minWidth('0')
    .textAlign('left')
    .boxShadow(colors.shadow)
    .transition('border-color 160ms, background 160ms, box-shadow 160ms')

  root.select('.metric-card:hover').borderColor(accent)

  root
    .select('.metric-card.selected')
    .borderColor(accent)
    .background(`linear-gradient(135deg,${colors.accentSoft},${surface})`)
    .boxShadow(`inset 0 3px 0 ${accent}, ${colors.shadow}`)

  root
    .select('.metric-label')
    .fontSize('11px')
    .color(muted)
    .display('block')
    .marginBottom('11px')

  root
    .select('.metric-value')
    .fontSize('27px')
    .letterSpacing('-0.8px')
    .fontWeight('600')
    .lineHeight('1.1')
    .display('block')

  root
    .select('.metric-change')
    .display('flex')
    .gap('6px')
    .fontSize('10px')
    .marginTop('14px')
    .color(muted)
    .alignItems('center')

  root
    .select('.delta')
    .padding('2px 5px')
    .borderRadius('4px')
    .background(colors.successSoft)
    .color(colors.success)
    .fontWeight('550')

  root
    .select('.delta.negative')
    .background(colors.dangerSoft)
    .color(colors.danger)

  root
    .select('.panel')
    .border(`1px solid ${edge}`)
    .borderRadius('10px')
    .background(surface)
    .minWidth('0')
    .overflow('hidden')
    .padding('0')
    .boxShadow(colors.shadow)

  root.select('.panel__body').minWidth('0')

  root.select('.totals-panel').margin('20px 0')

  root
    .select('.panel-heading')
    .padding('20px 23px')
    .display('flex')
    .justifyContent('space-between')
    .alignItems('center')
    .gap('12px')

  root
    .select('.panel h2')
    .fontSize('14px')
    .fontWeight('600')
    .letterSpacing('-0.2px')

  root
    .select('.panel-heading p, .panel-subtitle')
    .fontSize('10px')
    .color(muted)
    .marginTop('4px')

  root
    .select('.panel-link')
    .fontSize('10px')
    .color(muted)
    .display('flex')
    .alignItems('center')
    .gap('5px')
    .whiteSpace('nowrap')

  root.select('.panel-link:hover').color(accent)

  root
    .select('.overview-grid, .audience-grid')
    .display('grid')
    .gridTemplateColumns('repeat(2,minmax(0,1fr))')
    .gap('23px')
    .marginBottom('23px')
}
