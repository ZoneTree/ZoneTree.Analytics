import type { DashboardStyleContext } from './context'

export function registerDialogsStyles(context: DashboardStyleContext) {
  const { root, ink, muted, edge, surface, colors } = context
  root
    .select('dialog')
    .border(`1px solid ${edge}`)
    .borderRadius('12px')
    .background(surface)
    .color(ink)
    .width('min(470px,calc(100% - 32px))')
    .maxHeight('calc(100dvh - 32px)')
    .overflowY('auto')
    .padding('25px')
    .boxShadow('0 20px 80px #00000030')

  root
    .select('dialog::backdrop')
    .background(colors.overlay)
    .backdropFilter('blur(3px)')

  root
    .select('.dialog-heading')
    .display('flex')
    .justifyContent('space-between')
    .alignItems('center')
    .marginBottom('9px')

  root.select('.dialog-heading h2').fontSize('19px')

  root.select('dialog p').fontSize('12px').color(muted).marginBottom('18px')

  root.select('dialog label').marginBottom('15px')

  root
    .select('.dialog-actions')
    .display('flex')
    .justifyContent('end')
    .gap('9px')
    .marginTop('23px')

  root.select('.zt-dashboard .filter-form').display('grid').gap('18px')

  root.select('.filter-form .dialog-intro').display('grid').gap('8px')

  root.select('.filter-form .dialog-heading').marginBottom('0')

  root
    .select('.zt-dashboard .filter-form p')
    .margin('0')
    .fontSize('12px')
    .lineHeight('1.6')

  root.select('.zt-dashboard .filter-form .field-hint').fontSize('10px')

  root.select('.zt-dashboard .filter-form .form-block__field').margin('0')

  root.select('.filter-form .dialog-actions').marginTop('2px')
}
