import { sidebarColors as sidebar } from '../colors'
import type { DashboardStyleContext } from './context'

export function registerNavigationStyles(context: DashboardStyleContext) {
  const { root } = context
  // Style has no typed scrollbar-width method yet.
  root
    .select('.sidebar')
    .background(sidebar.canvas)
    .color(sidebar.ink)
    .position('sticky')
    .top('0')
    .height('100dvh')
    .padding('32px 18px 22px')
    .display('flex')
    .flexDirection('column')
    .zIndex('30')
    .overflowY('auto')
    .set('scrollbar-width', 'none')

  root.select('.sidebar::-webkit-scrollbar').display('none')

  root.select('.brand').padding('0 8px 29px')

  root
    .select('.workspace')
    .marginBottom('32px')
    .padding('13px 10px')
    .border(`1px solid ${sidebar.border}`)
    .borderRadius('8px')
    .display('flex')
    .alignItems('center')
    .gap('10px')
    .fontSize('12px')

  root
    .select('.workspace-mark')
    .width('30px')
    .height('30px')
    .display('grid')
    .placeItems('center')
    .background(sidebar.surface)
    .color(sidebar.ink)
    .borderRadius('6px')
    .fontWeight('600')

  root
    .select('.workspace strong')
    .fontWeight('550')
    .display('block')
    .maxWidth('134px')
    .overflow('hidden')
    .textOverflow('ellipsis')
    .whiteSpace('nowrap')

  root
    .select('.workspace small')
    .display('block')
    .color(sidebar.muted)
    .marginTop('2px')
    .fontSize('10px')

  root
    .select('.workspace-dot')
    .width('5px')
    .height('5px')
    .borderRadius('50%')
    .background(sidebar.mint)
    .marginLeft('auto')

  root
    .select('.nav-label')
    .fontSize('9px')
    .letterSpacing('1.5px')
    .color(sidebar.muted)
    .paddingLeft('14px')
    .marginBottom('11px')

  root.select('.nav-list').display('grid').gap('4px').marginBottom('26px')

  root
    .select('.nav-list a')
    .display('flex')
    .alignItems('center')
    .gap('12px')
    .borderRadius('6px')
    .padding('11px 13px')
    .color(sidebar.link)
    .fontSize('13px')
    .transition('background 150ms')

  root.select('.nav-list a:hover').background(sidebar.hover).color(sidebar.ink)

  root
    .select('.nav-list a[aria-current="page"]')
    .color(sidebar.accent)
    .background(sidebar.selection)
    .boxShadow(`inset 3px 0 0 ${sidebar.accent}`)
    .fontWeight('600')

  root
    .select('.sidebar-bottom')
    .marginTop('auto')
    .padding('24px 10px 0')
    .borderTop(`1px solid ${sidebar.border}`)

  root
    .select('.privacy-mark')
    .display('flex')
    .gap('9px')
    .alignItems('center')
    .fontSize('10px')
    .color(sidebar.link)

  root
    .select('.privacy-mark small')
    .display('block')
    .color(sidebar.muted)
    .fontSize('10px')
    .marginTop('3px')

  root
    .select('.sidebar-bottom > a')
    .marginTop('24px')
    .display('flex')
    .justifyContent('space-between')
    .alignItems('center')
    .fontSize('11px')
    .color(sidebar.link)

  for (const section of [
    'overview',
    'pages',
    'sources',
    'audience',
    'goals',
    'funnels',
    'bots',
    'explorer',
    'benchmark',
    'admin',
    'admin-sites',
    'admin-users',
    'admin-indexes',
    'admin-settings',
    'admin-audit',
  ]) {
    root
      .select(`body[data-section="${section}"] .nav-list .nav-${section}`)
      .color(sidebar.accent)
      .background(sidebar.selection)
      .boxShadow(`inset 3px 0 0 ${sidebar.accent}`)
      .fontWeight('600')
  }
}
