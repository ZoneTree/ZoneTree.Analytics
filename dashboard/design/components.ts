import { defineComponent, html, type RefOrValue } from 'regor'
import { defineDashboardLogo } from './logo'

export interface DashboardTopbar {
  title: RefOrValue<string>
}

export function defineDashboardComponents() {
  return {
    DashboardLogo: defineDashboardLogo(),
    DashboardTopbar: defineComponent<DashboardTopbar>(
      html`<header class="topbar">
  <div class="topbar-content">
  <div class="breadcrumb">
    <label
      for="nav-toggle"
      class="icon-button mobile-only"
      aria-label="Toggle navigation"
    >
      <Icon name="tabler:menu-2"/>
    </label>
    <span>Workspace</span>
    <span class="slash">/</span>
    <strong>{{ title }}</strong>
  </div>
  <div class="topbar-actions">
    <ThemeToggle/>
    <a
      class="topbar__icon topbar-github"
      href="https://github.com/ZoneTree/ZoneTree.Analytics"
      target="_blank"
      rel="noopener"
      aria-label="ZoneTree Analytics on GitHub"
      title="ZoneTree Analytics on GitHub"
    ><Icon name="tabler:brand-github"/></a>
    <span class="avatar" title="Local analytics workspace">ZT</span>
  </div>
  </div>
</header>`,
      { props: ['title'] },
    ),
  }
}
