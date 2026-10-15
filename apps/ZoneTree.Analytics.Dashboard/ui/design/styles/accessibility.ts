import type { DashboardStyleContext } from './context'

export function registerAccessibilityStyles(context: DashboardStyleContext) {
  const { root, surface, accent } = context
  root
    .select('.skip-link')
    .position('fixed')
    .top('12px')
    .left('12px')
    .transform('translateY(-200%)')
    .zIndex('100')
    .background(surface)
    .padding('12px 18px')
    .border(`1px solid ${accent}`)
    .borderRadius('6px')

  root.select('.skip-link:focus').transform('translateY(0)')

  root
    .select('.sr-only')
    .position('absolute')
    .width('1px')
    .height('1px')
    .padding('0')
    .overflow('hidden')
    .clip('rect(0,0,0,0)')
    .whiteSpace('nowrap')

  root.select('.mobile-only').display('none')
}
