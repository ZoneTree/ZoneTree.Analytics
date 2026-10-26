import type { DashboardStyleContext } from './context'

export function registerFormsStyles(context: DashboardStyleContext) {
  const { root, ink, muted, edge, surface, accent, colors } = context
  root.select('.form-panel').padding('25px').marginBottom('22px')

  root.select('.form-panel h2').marginBottom('7px')

  root
    .select('.form-panel > .panel__body > p')
    .color(muted)
    .fontSize('12px')
    .maxWidth('750px')
    .marginBottom('23px')

  root
    .select('.form-grid')
    .display('grid')
    .gridTemplateColumns('repeat(2,minmax(0,1fr))')
    .gap('18px')
    .marginBottom('20px')

  root
    .select(
      '.zt-dashboard label:not(.control):not(.check):not(.mobile-only):not(.nav-backdrop)',
    )
    .display('grid')
    .gap('7px')
    .color(muted)
    .fontSize('12px')

  root
    .select(
      '.zt-dashboard input, .zt-dashboard textarea, .zt-dashboard label:not(.control) > select',
    )
    .border(`1px solid ${edge}`)
    .borderRadius('6px')
    .padding('9px 11px')
    .background(surface)
    .color(ink)
    .width('100%')
    .minWidth('0')

  root
    .select('.zt-dashboard textarea')
    .fontFamily("'Cascadia Code', Consolas, monospace")
    .fontSize('12px')
    .lineHeight('1.7')
    .resize('vertical')

  root
    .select('.field-hint')
    .fontSize('10px')
    .color(muted)
    .marginTop('7px')
    .fontWeight('400')

  root
    .select('.form-actions')
    .display('flex')
    .alignItems('center')
    .gap('12px')
    .marginTop('20px')
    .flexWrap('wrap')

  root.select('.wide-field').gridColumn('1/-1')

  root.select('.zt-dashboard .funnel-form').gap('20px')

  root.select('.zt-dashboard .funnel-form .form-block__field').marginBottom('0')

  root.select('.funnel-form .funnel-step').marginBottom('0')

  root.select('.funnel-form .form-actions').marginTop('0').alignItems('end')

  root.select('.zt-dashboard .sample-data-form').gap('18px')

  root
    .select('.zt-dashboard .sample-data-form .form-block__field')
    .marginBottom('0')

  root
    .select('.sample-data-form .form-grid')
    .marginBottom('0')
    .gridTemplateColumns('repeat(3,minmax(0,1fr))')

  root.select('.sample-data-form .form-actions').marginTop('0')

  root.select('.sample-presets').display('flex').gap('8px').flexWrap('wrap')

  root
    .select('.sample-job-status')
    .fontSize('12px')
    .color(muted)
    .display('grid')
    .gap('8px')

  root
    .select('.sample-progress')
    .width('100%')
    .height('8px')
    .accentColor(accent)

  root
    .select('.sample-ingestion-metrics')
    .display('grid')
    .gridTemplateColumns('repeat(4,minmax(0,1fr))')
    .gap('16px')
    .margin('16px 0 0')

  root.select('.sample-ingestion-metrics dt').fontSize('11px').color(muted)

  root
    .select('.sample-ingestion-metrics dd')
    .margin('4px 0 0')
    .color(ink)
    .fontSize('22px')
    .fontWeight('600')
    .fontVariantNumeric('tabular-nums')

  root
    .select('.sample-ingestion-metrics small')
    .color(muted)
    .fontSize('11px')
    .fontWeight('400')

  root
    .select('.sample-benchmark-result')
    .borderTop(`1px solid ${edge}`)
    .marginTop('24px')
    .paddingTop('20px')

  root
    .select('.sample-benchmark-result h3')
    .fontSize('14px')
    .fontWeight('600')
    .marginBottom('8px')

  root.select('.sample-benchmark-result .field-hint').marginTop('12px')

  root
    .select('.funnel-step')
    .display('grid')
    .gridTemplateColumns('28px minmax(0,1fr) minmax(0,1fr) minmax(0,1fr) 34px')
    .gap('10px')
    .alignItems('end')
    .marginBottom('14px')

  root
    .select('.step-number')
    .background(colors.accentSoft)
    .borderRadius('50%')
    .color(accent)
    .width('25px')
    .height('25px')
    .display('grid')
    .placeItems('center')
    .marginBottom('7px')
}
