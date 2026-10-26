import { defineComponent, html, sref } from 'regor'
import type { AuditEntry } from '../../lib/admin-contracts'
import { formatDate, mountPage } from '../../lib/page'
import { dashboardFetch, loadMe } from '../../lib/session'

const pageSize = 100

const actionLabels: Record<string, string> = {
  'setup.completed': 'Completed setup',
  'auth.signin': 'Signed in',
  'auth.signin-failed': 'Failed sign-in',
  'auth.signout': 'Signed out',
  'auth.password-changed': 'Changed password',
  'settings.updated': 'Changed settings',
  'user.created': 'Created user',
  'user.updated': 'Updated user',
  'user.deleted': 'Deleted user',
  'user.sessions-ended': 'Signed a user out everywhere',
  'session.ended': 'Ended a session',
}

const template = html`<div class="page-heading">
  <div>
    <div class="eyebrow">ADMINISTRATION</div>
    <h1>Audit log</h1>
    <p>Sign-ins, setting changes and user management, newest first.</p>
  </div>
  <div class="heading-actions">
    <Btn
      class="button"
      variant="outline"
      icon="tabler:refresh"
      @click="reload"
      :disabled="loading"
      >Refresh</Btn>
  </div>
</div>
<FormStatus tone="danger" role="alert" r-if="error"
  >{{ error }}</FormStatus>
<Panel class="form-panel">
  <div class="table-wrap" r-if="entries.length">
    <table class="data-table audit-table">
      <thead>
        <tr>
          <th scope="col">Time</th>
          <th scope="col">Who</th>
          <th scope="col">What</th>
          <th scope="col">Details</th>
          <th scope="col">Address</th>
        </tr>
      </thead>
      <tbody>
        <tr
          r-for="entry in entries"
          :class="{ warning: entry.action === 'auth.signin-failed' }"
        >
          <td>{{ formatDate(entry.time) }}</td>
          <td>{{ entry.actor || '—' }}</td>
          <td>{{ label(entry.action) }}</td>
          <td>{{ entry.detail || '' }}</td>
          <td>{{ entry.ipAddress || '' }}</td>
        </tr>
      </tbody>
    </table>
  </div>
  <p r-if="!entries.length && !loading">Nothing has been recorded yet.</p>
  <div class="form-actions" r-if="more">
    <Btn class="button" variant="outline" @click="loadMore" :disabled="loading"
      >Show older entries</Btn>
  </div>
</Panel>`

const page = defineComponent(template, {
  context: () => {
    const entries = sref<AuditEntry[]>([])
    const error = sref('')
    const loading = sref(false)
    const more = sref(false)
    const fetchPage = async (before: string | null) => {
      loading(true)
      try {
        const query = new URLSearchParams({ take: String(pageSize) })
        if (before) query.set('before', before)
        const next = await dashboardFetch<AuditEntry[]>(`/admin/audit?${query}`)
        entries(before ? [...entries(), ...next] : next)
        more(next.length === pageSize)
        error('')
      } catch (failure) {
        error((failure as Error).message)
      } finally {
        loading(false)
      }
    }
    void loadMe()
      .then(() => fetchPage(null))
      .catch((failure: Error) => error(failure.message))
    return {
      entries,
      error,
      loading,
      more,
      formatDate,
      label: (action: string) => actionLabels[action] ?? action,
      reload: () => fetchPage(null),
      loadMore: () => fetchPage(entries()[entries().length - 1]?.id ?? null),
    }
  },
})

mountPage({ selector: '#admin-audit-app', component: page })
