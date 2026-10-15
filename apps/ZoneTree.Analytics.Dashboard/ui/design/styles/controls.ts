import type { DashboardStyleContext } from './context'

export function registerControlsStyles(context: DashboardStyleContext) {
  const { root, palette, ink, muted, edge, surface, accent, dark, colors } =
    context
  root
    .select('.button, .icon-button')
    .border(`1px solid ${edge}`)
    .background(surface)
    .color(ink)
    .borderRadius('6px')
    .padding('8px 12px')
    .display('inline-flex')
    .alignItems('center')
    .justifyContent('center')
    .gap('7px')
    .fontSize('11px')
    .minHeight('35px')
    .fontWeight('500')

  root.select('.button:hover, .icon-button:hover').background(colors.hover)

  root.select('.icon-button').padding('7px').width('35px')

  root
    .select('.button.primary')
    .background(accent)
    .color(colors.accentInk)
    .borderColor(accent)

  root
    .select('.button.primary:hover')
    .background(colors.accentHover)
    .borderColor(colors.accentHover)

  root
    .select('.button.danger')
    .color(colors.danger)
    .borderColor(colors.dangerBorder)
    .background(colors.dangerSoft)

  root
    .select('.button.quiet')
    .background('transparent')
    .borderColor('transparent')
    .color(muted)

  root
    .select('.toolbar')
    .display('flex')
    .justifyContent('space-between')
    .gap('15px')
    .alignItems('center')
    .paddingBottom('21px')
    .borderBottom(`1px solid ${edge}`)
    .marginBottom('22px')

  root
    .select('.toolbar-left, .toolbar-right')
    .display('flex')
    .gap('10px')
    .alignItems('center')
    .flexWrap('wrap')

  root
    .select('.control')
    .display('flex')
    .alignItems('center')
    .gap('8px')
    .border(`1px solid ${edge}`)
    .borderRadius('6px')
    .padding('0 10px')
    .minHeight('35px')
    .background(surface)

  root.select('.control .icon').color(muted).width('15px').height('15px')

  root
    .select('.control select')
    .border('0')
    .background('transparent')
    .color(ink)
    .fontSize('11px')
    .padding('6px 18px 6px 0')
    .maxWidth('190px')

  root
    .select('.compare-control')
    .background('transparent')
    .borderColor('transparent')

  root
    .select('.control:has(.form-block__input:focus-visible)')
    .borderColor(accent)
    .boxShadow(`0 0 0 1px ${accent}`)

  root
    .select('.custom-range, .filter-chips')
    .display('flex')
    .gap('10px')
    .flexWrap('wrap')
    .alignItems('end')
    .marginBottom('16px')

  root.select('.filter-chips:empty, .report-status:empty').display('none')

  root
    .select('.filter-chip')
    .background(colors.accentSoft)
    .color(accent)
    .border(`1px solid ${edge}`)
    .borderRadius('5px')
    .padding('6px 9px')
    .fontSize('11px')

  root
    .select('.zt-dashboard .form-block')
    .padding('0')
    .border('0')
    .background('transparent')
    .boxShadow('none')

  root
    .select('.control .form-block__input-shell')
    .border('0')
    .background('transparent')
    .boxShadow('none')
    .padding('0')
    .minHeight('30px')

  root
    .select(
      '.control .form-block__input-shell:has(.form-block__input:focus-visible)',
    )
    .boxShadow('none')

  root
    .select('.control .form-block__input')
    .width('auto')
    .minWidth('0')
    .border('0')
    .padding('6px 18px 6px 0')
    .background('transparent')
    .fontSize('11px')

  root
    .select('.control .form-block__input-icon')
    .width('12px')
    .height('12px')
    .right('0')

  root
    .select('.control .form-block__select')
    .minWidth('96px')
    .paddingRight('24px')

  root.select('.control .form-block__field').gap('0').margin('0')

  root.select('.zt-dashboard .form-block__select-shell').overflow('visible')

  // Keep PureStack's native select and Regor model; style the browser picker.
  root
    .select('.zt-dashboard select.form-block__select::picker(select)')
    .background(surface)
    .color(ink)
    .border(`1px solid ${edge}`)
    .borderRadius('10px')
    .padding('6px')
    .minWidth('max(200px,anchor-size(width))')
    .maxWidth('calc(100vw - 24px)')
    .maxHeight('min(340px,60vh)')
    .overflowY('auto')
    .margin('8px 0 0')
    .boxShadow(dark ? '0 12px 36px #00000055' : '0 12px 36px #18181B18')
    .fontFamily(palette.font.family.base)
    .fontSize('13px')
    .lineHeight('1.5')

  root
    .select('.zt-dashboard select.form-block__select::picker-icon')
    .display('none')

  root
    .select('.zt-dashboard select.form-block__select option')
    .background(surface)
    .color(ink)
    .display('flex')
    .alignItems('center')
    .gap('12px')
    .minHeight('40px')
    .padding('10px 12px')
    .border('0')
    .borderRadius('6px')
    .cursor('pointer')
    .fontSize('13px')
    .lineHeight('1.5')

  root
    .select(
      '.zt-dashboard select.form-block__select option:is(:hover,:focus-visible)',
    )
    .background(colors.hover)
    .outline('none')

  root
    .select('.zt-dashboard select.form-block__select option:checked')
    .background(colors.accentSoft)
    .color(accent)
    .fontWeight('600')

  root
    .select('.zt-dashboard select.form-block__select option:disabled')
    .color(muted)
    .cursor('not-allowed')

  root
    .select('.zt-dashboard select.form-block__select option::checkmark')
    .color(accent)
    .order('1')
    .marginLeft('auto')
    .fontSize('12px')

  root.select('.control:has(select:open)').borderColor(accent)

  root
    .select('.zt-dashboard .form-panel .form-block__field')
    .marginBottom('12px')

  root
    .select('.zt-dashboard select.form-block__select')
    .appearance('base-select')
  root
    .select('.zt-dashboard select.form-block__select::picker(select)')
    .appearance('base-select')
  for (const id of ['site-select', 'range-select', 'compare-select']) {
    // Escape the leading dash: this is an anchor identifier, not a CSS variable.
    const anchor = `\\--zt-${id}`
    // CSS anchor positioning has no typed Style methods yet.
    root.select(`.control:has(#${id})`).set('anchor-name', anchor)
    root
      .select(`.zt-dashboard #${id}::picker(select)`)
      .set('position-anchor', anchor)
      .left('anchor(left)')
      .top('anchor(bottom)')
  }
}
