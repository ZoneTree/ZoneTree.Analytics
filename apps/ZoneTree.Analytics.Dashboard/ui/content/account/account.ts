import { computed, defineComponent, html, sref } from 'regor'
import type { MeResponse, SessionView } from '../lib/admin-contracts'
import {
  describeAgent,
  formatDate,
  mountPage,
  roleDescriptions,
} from '../lib/page'
import { dashboardFetch, loadMe } from '../lib/session'

const template = html`<div class="page-heading">
  <div>
    <div class="eyebrow">YOUR ACCOUNT</div>
    <h1>{{ me ? me.user.displayName : 'Account' }}</h1>
    <p>Your profile and the browsers signed in to your account.</p>
  </div>
  <div class="heading-actions">
    <a class="button" href="/account/settings/">Edit profile and password</a>
  </div>
</div>
<FormStatus tone="danger" role="alert" r-if="error">{{ error }}</FormStatus>
<div class="admin-columns" r-if="me">
  <Panel class="form-panel">
    <h2>Profile</h2>
    <dl class="detail-list">
      <div><dt>User name</dt><dd>{{ me.user.userName }}</dd></div>
      <div><dt>Display name</dt><dd>{{ me.user.displayName }}</dd></div>
      <div><dt>Role</dt><dd>{{ me.user.role }}<small>{{ roleDescription }}</small></dd></div>
      <div><dt>Sites</dt><dd>{{ siteList }}</dd></div>
      <div><dt>Member since</dt><dd>{{ formatDate(me.user.created) }}</dd></div>
    </dl>
  </Panel>
  <Panel class="form-panel">
    <h2>Signed-in browsers</h2>
    <p>End a session you don't recognize. Changing your password ends every other session.</p>
    <ul class="session-list">
      <li r-for="session in sessions">
        <div>
          <strong>{{ describeAgent(session.userAgent) }}</strong>
          <Badge tone="success" variant="surface" r-if="session.current">This browser</Badge>
          <small>{{ session.ipAddress || 'Unknown address' }} · last active {{ formatDate(session.lastSeen) }}</small>
        </div>
        <Btn class="button quiet" variant="none" r-if="!session.current" @click="revoke(session)">Sign out</Btn>
      </li>
    </ul>
  </Panel>
</div>`

const page = defineComponent(template, {
  context: () => {
    const me = sref<MeResponse | null>(null)
    const sessions = sref<SessionView[]>([])
    const error = sref('')
    const refresh = async () => {
      try {
        me(await loadMe())
        sessions(await dashboardFetch<SessionView[]>('/auth/me/sessions'))
      } catch (failure) {
        error((failure as Error).message)
      }
    }
    void refresh()
    return {
      me,
      sessions,
      error,
      formatDate,
      describeAgent,
      roleDescription: computed(
        () => roleDescriptions[me()?.user.role ?? ''] ?? '',
      ),
      siteList: computed(() => {
        const current = me()
        if (!current) return ''
        if (current.user.allSites) return 'All sites'
        return (
          current.sites.map((site) => site.name).join(', ') || 'No sites yet'
        )
      }),
      revoke: async (session: SessionView) => {
        try {
          await dashboardFetch(
            `/auth/me/sessions/${encodeURIComponent(session.id)}`,
            { method: 'DELETE' },
          )
          await refresh()
        } catch (failure) {
          error((failure as Error).message)
        }
      },
    }
  },
})

mountPage({ selector: '#account-app', component: page })
