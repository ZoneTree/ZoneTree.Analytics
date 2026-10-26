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
      <SignIn class="topbar-account">
        <template name="avatar">
          <span class="avatar" data-user-initials aria-hidden="true"></span>
        </template>
        <Flex
          container="nav"
          direction="column"
          align="stretch"
          class="gap-1"
          aria-label="Account"
        >
          <BtnLink
            class="sign-in__signed-out-action justify-start w-full text-start"
            href="/signin/"
            variant="subtleBtn"
            icon="lucide:log-in"
            >Sign in</BtnLink>
          <BtnLink
            class="sign-in__signed-in-action justify-start w-full text-start"
            href="/account/"
            variant="subtleBtn"
            icon="lucide:user-round"
            >Account</BtnLink>
          <BtnLink
            class="sign-in__signed-in-action justify-start w-full text-start"
            href="/account/settings/"
            variant="subtleBtn"
            icon="lucide:settings"
            >Account settings</BtnLink>
          <BtnLink
            class="admin-only justify-start w-full text-start"
            href="/admin/"
            variant="subtleBtn"
            icon="tabler:server"
            >Administration</BtnLink>
          <BtnLink
            class="sign-in__signed-in-action justify-start w-full text-start"
            href="/signout/"
            variant="subtleBtn"
            icon="lucide:log-out"
            >Sign out</BtnLink>
        </Flex>
      </SignIn>
    </div>
  </div>
</header>`,
      { props: ['title'] },
    ),
    // The brand column of the sign-in, sign-out and setup pages.
    AuthAside: defineComponent(
      html`<aside class="auth-aside">
  <DashboardLogo href="/" icon="iconoir:cube-hole"/>
  <div class="auth-pitch">
    <h2>Analytics you own.</h2>
    <p>
      Private website analytics on your own infrastructure, stored in ZoneTree.
    </p>
  </div>
  <div class="privacy-mark">
    <Icon name="tabler:shield-check"/><span
      >Your data. Your infrastructure.<small>Powered by ZoneTree</small></span>
  </div>
</aside>
<div class="auth-actions"><ThemeToggle/></div>`,
    ),
  }
}
