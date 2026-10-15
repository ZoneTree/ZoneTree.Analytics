import type { DashboardStyleContext } from './context'

export function registerChartsStyles(context: DashboardStyleContext) {
  const { root, neutral, muted, edge, accent, colors } = context
  root
    .select('.chart-data summary')
    .padding('0 23px 16px')
    .color(muted)
    .cursor('pointer')
    .fontSize('10px')

  root.select('.traffic-panel').marginBottom('23px')

  root
    .select('.chart-controls')
    .display('flex')
    .gap('2px')
    .padding('3px')
    .background(neutral.canvas)
    .border(`1px solid ${edge}`)
    .borderRadius('6px')

  root
    .select('.chart-controls button')
    .border('0')
    .color(muted)
    .background('transparent')
    .padding('4px 9px')
    .borderRadius('4px')
    .fontSize('10px')

  root
    .select('.chart-controls button[aria-pressed="true"]')
    .color(accent)
    .background(colors.accentSoft)
    .boxShadow(colors.shadow)

  root.select('.chart-wrap').padding('0 15px 0 8px').height('263px')

  root.select('.chart-wrap .line-chart').width('100%').height('100%')

  root.select('.chart-wrap .line-chart__area').opacity('0.10')

  root.select('.chart-wrap .line-chart__area:not(:first-child)').display('none')

  root.select('.chart-wrap .line-chart__line').strokeWidth('0.65')

  root
    .select('.chart-wrap .line-chart__line:not(:first-child)')
    .strokeDasharray('1.3 1.1')
    .opacity('0.65')

  root
    .select('.chart-legend')
    .display('flex')
    .alignItems('center')
    .gap('19px')
    .padding('4px 23px 19px')
    .color(muted)
    .fontSize('10px')

  root
    .select('.legend-mark')
    .display('inline-block')
    .width('16px')
    .borderTop('2px solid var(--zt-chart-current)')
    .marginRight('6px')
    .verticalAlign('middle')

  root
    .select('.legend-mark.previous')
    .borderTop('2px dashed var(--zt-chart-previous)')

  root
    .select('.device-layout')
    .display('flex')
    .alignItems('center')
    .justifyContent('center')
    .gap('25px')
    .padding('0 23px 24px')

  root.select('.device-chart').width('160px').height('160px').flexShrink('0')

  root.select('.device-legend').display('grid').gap('14px').fontSize('11px')

  root
    .select('.device-legend div')
    .display('flex')
    .alignItems('center')
    .gap('9px')

  root.select('.device-legend i').width('8px').height('8px').borderRadius('2px')

  root.select('.device-legend strong').fontWeight('600').marginLeft('15px')

  root.select('.funnel-results').padding('24px')

  root.select('.funnel-result').marginBottom('20px')

  root
    .select('.funnel-result-header')
    .display('flex')
    .justifyContent('space-between')
    .marginBottom('7px')
    .fontSize('12px')

  root
    .select('.funnel-bar-track')
    .background(neutral.canvas)
    .height('34px')
    .borderRadius('5px')
    .overflow('hidden')

  root
    .select('.funnel-bar')
    .height('100%')
    .background(colors.chartCurrent)
    .minWidth('0')
    .width('var(--bar-width)')

  root
    .select('.result-json')
    .padding('20px')
    .fontFamily('Consolas, monospace')
    .fontSize('11px')
    .overflow('auto')
    .maxHeight('360px')
    .color(muted)
    .borderTop(`1px solid ${edge}`)
}
