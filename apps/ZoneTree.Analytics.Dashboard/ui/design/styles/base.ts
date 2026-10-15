import type { DashboardStyleContext } from './context'

export function registerBaseStyles(context: DashboardStyleContext) {
  const { root, theme, palette, neutral, ink, accent, colors } = context
  // Style has no typed methods for color-scheme or font smoothing yet.
  root.select(':scope').set('color-scheme', theme)

  root
    .select('body.zt-dashboard')
    .set('--zt-chart-current', colors.chartCurrent)
    .set('--zt-chart-previous', colors.chartPrevious)
    .set('--zt-chart-gold', colors.chartGold)
    .set('--zt-chart-rose', colors.chartRose)
    .margin('0')
    .background(neutral.canvas)
    .color(ink)
    .fontFamily(palette.font.family.base)
    .fontSize('14px')
    .lineHeight('1.5')
    .set('-webkit-font-smoothing', 'antialiased')

  root
    .select('.zt-dashboard *, .zt-dashboard *::before, .zt-dashboard *::after')
    .boxSizing('border-box')

  root.select('.zt-dashboard :is(h1,h2,h3,p)').margin('0')

  root.select('.zt-dashboard a').color('inherit').textDecoration('none')

  root
    .select(
      '.zt-dashboard button, .zt-dashboard input, .zt-dashboard select, .zt-dashboard textarea',
    )
    .font('inherit')

  root.select('.zt-dashboard button').cursor('pointer')

  root.select('.zt-dashboard [hidden]').display('none !important')

  root
    .select(
      '.zt-dashboard :is(button,a,input,select,textarea,summary):not(.form-block__input):focus-visible',
    )
    .outline(`2px solid ${accent}`)
    .outlineOffset('4px')

  root.select('.zt-dashboard button:disabled').opacity('0.5').cursor('wait')

  root
    .select('.zt-dashboard .icon, .zt-dashboard .icon svg')
    .width('19px')
    .height('19px')
    .flexShrink('0')
}
