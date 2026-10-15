import type { DashboardStyleContext } from './context'

export function registerTablesStyles(context: DashboardStyleContext) {
  const { root, neutral, ink, muted, edge, accent, colors } = context
  root.select('.breakdown-list').padding('0 23px 18px')

  root
    .select('.list-heading')
    .display('flex')
    .justifyContent('space-between')
    .color(muted)
    .fontSize('9px')
    .padding('0 0 9px')
    .borderBottom(`1px solid ${edge}`)
    .letterSpacing('0.4px')

  root
    .select('.breakdown-row')
    .position('relative')
    .width('100%')
    .padding('11px 8px')
    .display('flex')
    .alignItems('center')
    .justifyContent('space-between')
    .gap('12px')
    .border('0')
    .borderBottom(`1px solid ${edge}`)
    .background('transparent')
    .color(ink)
    .textAlign('left')
    .fontSize('11px')
    .isolation('isolate')
    .minHeight('40px')

  root
    .select('.breakdown-row::before')
    .content('""')
    .position('absolute')
    .inset('5px auto 5px 0')
    .width('var(--bar-width,0%)')
    .background(colors.accentSoft)
    .borderRadius('4px')
    .zIndex('-1')

  root.select('.breakdown-row:hover::before').background(colors.accentSoftHover)

  root
    .select('.row-name')
    .display('flex')
    .alignItems('center')
    .gap('9px')
    .minWidth('0')
    .overflow('hidden')
    .textOverflow('ellipsis')
    .whiteSpace('nowrap')

  root
    .select('.row-name .initial')
    .width('20px')
    .height('20px')
    .borderRadius('5px')
    .display('grid')
    .placeItems('center')
    .fontSize('9px')
    .fontWeight('600')
    .background(colors.accentSoftHover)
    .color(accent)
    .flexShrink('0')

  root
    .select('.row-count')
    .fontVariantNumeric('tabular-nums')
    .fontWeight('600')
    .whiteSpace('nowrap')

  root
    .select('.row-count small')
    .color(muted)
    .fontWeight('400')
    .fontSize('9px')
    .marginLeft('10px')
    .display('inline-block')
    .minWidth('36px')
    .textAlign('right')

  root.select('.table-wrap').overflowX('auto').width('100%')

  root
    .select('.data-table')
    .width('100%')
    .borderCollapse('collapse')
    .textAlign('left')
    .fontSize('12px')

  root
    .select('.data-table th')
    .padding('12px 22px')
    .color(muted)
    .fontWeight('500')
    .fontSize('10px')
    .background(neutral.canvas)
    .borderBottom(`1px solid ${edge}`)
    .whiteSpace('nowrap')

  root
    .select('.data-table td')
    .padding('13px 22px')
    .borderBottom(`1px solid ${edge}`)
    .fontVariantNumeric('tabular-nums')
    .whiteSpace('nowrap')
    .maxWidth('340px')
    .overflow('hidden')
    .textOverflow('ellipsis')

  root.select('.data-table tbody tr:last-child td').borderBottom('0')

  root.select('.data-table tbody tr:hover').background(colors.hover)

  root
    .select('.data-table button')
    .background('transparent')
    .border('0')
    .color(accent)
    .padding('0')
    .textAlign('left')

  root.select('.data-table .numeric').textAlign('right')

  root
    .select('.pagination')
    .display('flex')
    .justifyContent('space-between')
    .alignItems('center')
    .padding('14px 22px')
    .borderTop(`1px solid ${edge}`)
    .color(muted)
    .fontSize('11px')

  root.select('.pagination > div').display('flex').gap('8px')

  root
    .select('.tabs')
    .display('flex')
    .gap('8px')
    .flexWrap('wrap')
    .padding('0 23px 16px')

  root
    .select('.tabs button')
    .border(`1px solid ${edge}`)
    .background('transparent')
    .color(muted)
    .padding('6px 12px')
    .borderRadius('5px')
    .fontSize('11px')

  root
    .select('.tabs button[aria-pressed="true"]')
    .background(colors.accentSoft)
    .color(accent)
    .borderColor(accent)
}
