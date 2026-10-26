import { defineComponent, html, type SRef, sref } from 'regor'
import { loadAdmin } from '../../lib/admin'
import type { SessionView, SiteView, UserView } from '../../lib/admin-contracts'
import {
  describeAgent,
  formatDate,
  mountPage,
  roleDescriptions,
} from '../../lib/page'
import { dashboardFetch } from '../../lib/session'

interface SiteChoice {
  id: string
  name: string
  checked: SRef<boolean>
}

// The create and edit form: one draft at a time.
interface UserDraft {
  id: string | null
  administrator: boolean
  userName: SRef<string>
  displayName: SRef<string>
  role: SRef<string>
  allSites: SRef<boolean>
  disabled: SRef<boolean>
  password: SRef<string>
  sites: SiteChoice[]
}

const roleOptions = [
  { value: 'Viewer', label: 'Viewer: reads reports' },
  { value: 'Analyst', label: 'Analyst: reports and query explorer' },
]

const template = html`<div class="page-heading">
  <div>
    <div class="eyebrow">ADMINISTRATION</div>
    <h1>Users</h1>
    <p>Everyone who can sign in, what they can do, and which sites they see.</p>
  </div>
  <div class="heading-actions">
    <Btn
      class="button primary"
      tone="accent"
      icon="tabler:user-plus"
      @click="create"
      r-if="!draft"
      >Add user</Btn>
  </div>
</div>
<FormStatus tone="danger" role="alert" r-if="error"
  >{{ error }}</FormStatus>
<FormStatus tone="success" r-if="status">{{ status }}</FormStatus>

<Panel class="form-panel" r-if="draft">
  <h2>{{ draft.id ? 'Edit ' + draft.userName : 'Add a user' }}</h2>
  <p r-if="!draft.id">
    Share the user name and password with the person; they can change the
    password after signing in.
  </p>
  <AppForm @submit.prevent="save">
    <div class="form-grid">
      <FormInputField
        id="user-name"
        label="User name"
        :model="draft.userName"
        :required="true"
        autocomplete="off"/>
      <FormInputField
        id="user-display"
        label="Display name"
        :model="draft.displayName"
        placeholder="Optional"/>
      <label r-if="!draft.administrator">
        Role
        <select r-model="draft.role">
          <option r-for="option in roleOptions" :value="option.value">
            {{ option.label }}
          </option>
        </select>
      </label>
      <FormInputField
        id="user-password"
        :label="draft.id ? 'New password' : 'Password'"
        type="password"
        :model="draft.password"
        :required="!draft.id"
        autocomplete="new-password"
        :placeholder="draft.id ? 'Leave empty to keep the current password' : 'At least 10 characters'"
        r-if="!draft.administrator"/>
    </div>
    <p class="field-hint" r-if="draft.administrator">
      Change your own password in
      <a href="/account/settings/">Account settings</a>.
    </p>
    <fieldset class="site-access" r-if="!draft.administrator">
      <legend>Sites</legend>
      <label class="check">
        <input type="checkbox" r-model="draft.allSites"/>
        <span>All sites, including sites added later</span>
      </label>
      <div class="setting-choices" r-if="!draft.allSites">
        <label class="check" r-for="site in draft.sites">
          <input type="checkbox" r-model="site.checked"/>
          <span>{{ site.name }}</span>
        </label>
      </div>
    </fieldset>
    <label class="check" r-if="draft.id && !draft.administrator">
      <input type="checkbox" r-model="draft.disabled"/>
      <span>Disabled: cannot sign in</span>
    </label>
    <p class="field-hint" r-if="draft.id">
      Changing the role, sites, password or status signs the user out
      everywhere.
    </p>
    <div class="form-actions">
      <Btn class="button primary" tone="accent" type="submit" :disabled="busy"
        >{{ draft.id ? 'Save user' : 'Create user' }}</Btn>
      <Btn class="button" variant="outline" @click="close" :disabled="busy"
        >Cancel</Btn>
      <Btn
        class="button quiet danger"
        variant="none"
        icon="tabler:trash"
        r-if="draft.id && !draft.administrator"
        @click="remove"
        :disabled="busy"
        >Delete user</Btn>
    </div>
  </AppForm>
  <div class="user-sessions" r-if="draft.id">
    <h3>Signed-in browsers</h3>
    <ul class="session-list" r-if="sessions.length">
      <li r-for="session in sessions">
        <div>
          <strong>{{ describeAgent(session.userAgent) }}</strong>
          <Badge tone="success" variant="surface" r-if="session.current"
            >You, now</Badge>
          <small
            >{{ session.ipAddress || 'Unknown address' }} · last active
            {{ formatDate(session.lastSeen) }}</small>
        </div>
        <Btn
          class="button quiet"
          variant="none"
          r-if="!session.current"
          @click="endSession(session)"
          >Sign out</Btn>
      </li>
    </ul>
    <p r-else>No active sessions.</p>
    <Btn
      class="button"
      variant="outline"
      icon="tabler:logout"
      r-if="sessions.length"
      @click="endAll"
      >Sign out everywhere</Btn>
  </div>
</Panel>

<Panel class="form-panel">
  <div class="table-wrap">
    <table class="data-table users-table">
      <thead>
        <tr>
          <th scope="col">User</th>
          <th scope="col">Role</th>
          <th scope="col">Sites</th>
          <th scope="col">Last sign-in</th>
          <th scope="col"><span class="sr-only">Actions</span></th>
        </tr>
      </thead>
      <tbody>
        <tr r-for="user in users" :class="{ disabled: user.disabled }">
          <td>
            <strong>{{ user.displayName }}</strong>
            <small
              >{{ user.userName }}{{ user.disabled ? ' · disabled' : '' }}</small>
          </td>
          <td :title="roleDescriptions[user.role]">
            {{ user.role }}
          </td>
          <td>{{ siteSummary(user) }}</td>
          <td>{{ formatDate(user.lastSignIn) }}</td>
          <td class="numeric">
            <Btn
              class="button quiet"
              variant="none"
              icon="tabler:pencil"
              @click="edit(user)"
              >Edit</Btn>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</Panel>`

const page = defineComponent(template, {
  context: () => {
    const users = sref<UserView[]>([])
    const sites = sref<SiteView[]>([])
    const sessions = sref<SessionView[]>([])
    const draft = sref<UserDraft | null>(null)
    const error = sref('')
    const status = sref('')
    const busy = sref(false)

    const refresh = async () =>
      users(await dashboardFetch<UserView[]>('/admin/users'))
    const loadSessions = async (id: string) =>
      sessions(
        await dashboardFetch<SessionView[]>(
          `/admin/users/${encodeURIComponent(id)}/sessions`,
        ),
      )
    const open = (user: UserView | null) => {
      error('')
      status('')
      const selected = new Set(user?.sites ?? [])
      draft({
        id: user?.id ?? null,
        administrator: user?.role === 'Administrator',
        userName: sref(user?.userName ?? ''),
        displayName: sref(user?.displayName ?? ''),
        role: sref<string>(user?.role ?? 'Viewer'),
        allSites: sref(user?.allSites ?? false),
        disabled: sref(user?.disabled ?? false),
        password: sref(''),
        sites: sites().map((site) => ({
          ...site,
          checked: sref(selected.has(site.id)),
        })),
      })
      sessions([])
      if (user)
        void loadSessions(user.id).catch((failure: Error) =>
          error(failure.message),
        )
    }
    const guard = async (action: () => Promise<void>) => {
      error('')
      status('')
      busy(true)
      try {
        await action()
      } catch (failure) {
        error((failure as Error).message)
      } finally {
        busy(false)
      }
    }

    void loadAdmin()
      .then(async (context) => {
        sites(
          context.settings.sites.items.map((site) => ({
            id: site.id,
            name: site.name,
          })),
        )
        await refresh()
      })
      .catch((failure: Error) => error(failure.message))

    return {
      users,
      sessions,
      draft,
      error,
      status,
      busy,
      roleOptions,
      roleDescriptions,
      formatDate,
      describeAgent,
      siteSummary: (user: UserView) =>
        user.allSites
          ? 'All sites'
          : user.sites
              .map((id) => sites().find((site) => site.id === id)?.name ?? id)
              .join(', ') || 'None',
      create: () => open(null),
      edit: (user: UserView) => {
        open(user)
        window.scrollTo({ top: 0, behavior: 'smooth' })
      },
      close: () => draft(null),
      save: () =>
        guard(async () => {
          const current = draft() as UserDraft
          const body = {
            userName: current.userName().trim(),
            displayName: current.displayName().trim(),
            role: current.administrator ? null : current.role(),
            allSites: current.administrator ? null : current.allSites(),
            sites: current.administrator
              ? null
              : current.sites
                  .filter((site) => site.checked())
                  .map((site) => site.id),
            disabled:
              current.id && !current.administrator ? current.disabled() : null,
            password: current.password() ? current.password() : null,
          }
          const saved = current.id
            ? await dashboardFetch<UserView>(
                `/admin/users/${encodeURIComponent(current.id)}`,
                { method: 'PUT', body },
              )
            : await dashboardFetch<UserView>('/admin/users', { body })
          await refresh()
          draft(null)
          status(
            current.id
              ? `Saved ${saved.userName}.`
              : `Created ${saved.userName}. Share the password with them securely.`,
          )
        }),
      remove: () =>
        guard(async () => {
          const current = draft() as UserDraft
          if (
            !current.id ||
            !window.confirm(
              `Delete ${current.userName()}? They are signed out at once.`,
            )
          )
            return
          await dashboardFetch(
            `/admin/users/${encodeURIComponent(current.id)}`,
            { method: 'DELETE' },
          )
          await refresh()
          draft(null)
          status(`Deleted ${current.userName()}.`)
        }),
      endSession: (session: SessionView) =>
        guard(async () => {
          await dashboardFetch(
            `/admin/sessions/${encodeURIComponent(session.id)}`,
            { method: 'DELETE' },
          )
          await loadSessions((draft() as UserDraft).id as string)
        }),
      endAll: () =>
        guard(async () => {
          const id = (draft() as UserDraft).id as string
          await dashboardFetch(
            `/admin/users/${encodeURIComponent(id)}/sessions`,
            { method: 'DELETE' },
          )
          await loadSessions(id)
          status('Signed out everywhere.')
        }),
    }
  },
})

mountPage({ selector: '#admin-users-app', component: page })
