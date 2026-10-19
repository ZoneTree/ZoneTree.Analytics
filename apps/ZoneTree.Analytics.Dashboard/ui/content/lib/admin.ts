import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  type RefOrValue,
  type SRef,
  sref,
} from 'regor'
import type {
  ChangeView,
  DashboardSettings,
  MeResponse,
  SettingsSchema,
} from './admin-contracts'
import { DashboardApiError, dashboardFetch, loadMe } from './session'
import { cloneSettings, tierLabels } from './settings-model'

export interface AdminContext {
  me: MeResponse
  settings: DashboardSettings
  schema: SettingsSchema
  restartRequired: boolean
}

export async function loadAdmin(): Promise<AdminContext> {
  const [me, stored, schema] = await Promise.all([
    loadMe(),
    dashboardFetch<{ settings: DashboardSettings; restartRequired: boolean }>(
      '/admin/settings',
    ),
    dashboardFetch<SettingsSchema>('/admin/schema'),
  ])
  return {
    me,
    settings: stored.settings,
    schema,
    restartRequired: stored.restartRequired,
  }
}

interface SaveResult {
  settings: DashboardSettings
  changes: ChangeView[]
  reopened: boolean
  restartRequired: boolean
}

// Review, then save: the administrator sees what changes, when it applies and what it deletes.
export interface SaveFlow {
  reviewing: SRef<boolean>
  busy: SRef<boolean>
  changes: SRef<ChangeView[]>
  warnings: SRef<string[]>
  status: SRef<string>
  error: SRef<string>
  reopens: ComputedRef<boolean>
  review: (build: () => DashboardSettings) => Promise<void>
  confirm: () => Promise<void>
  cancel: () => void
  tierLabel: (tier: string) => string
}

export function createSaveFlow(
  onSaved: (settings: DashboardSettings, result: SaveResult) => void,
): SaveFlow {
  const reviewing = sref(false)
  const busy = sref(false)
  const changes = sref<ChangeView[]>([])
  const warnings = sref<string[]>([])
  const status = sref('')
  const error = sref('')
  let pending: DashboardSettings | null = null
  return {
    reviewing,
    busy,
    changes,
    warnings,
    status,
    error,
    reopens: computed(() =>
      changes().some((change) => change.tier === 'Reopen'),
    ),
    review: async (build) => {
      status('')
      error('')
      try {
        pending = cloneSettings(build())
        busy(true)
        const review = await dashboardFetch<{
          changes: ChangeView[]
          warnings: string[]
        }>('/admin/settings/review', { body: { settings: pending } })
        if (!review.changes.length) {
          status('Nothing changed.')
          return
        }
        changes(review.changes)
        warnings(review.warnings)
        reviewing(true)
      } catch (failure) {
        error((failure as Error).message)
      } finally {
        busy(false)
      }
    },
    confirm: async () => {
      if (!pending) return
      error('')
      busy(true)
      try {
        const result = await dashboardFetch<SaveResult>('/admin/settings', {
          method: 'PUT',
          body: { settings: pending, confirmDataLoss: warnings().length > 0 },
        })
        reviewing(false)
        status(
          result.reopened
            ? 'Saved. The engine reopened with the new settings; index work continues in the background.'
            : result.restartRequired
              ? 'Saved. Restart the server to apply the reporting concurrency limit.'
              : 'Saved.',
        )
        onSaved(result.settings, result)
      } catch (failure) {
        if (
          failure instanceof DashboardApiError &&
          Array.isArray(failure.details.warnings)
        )
          warnings(failure.details.warnings as string[])
        error((failure as Error).message)
      } finally {
        busy(false)
      }
    },
    cancel: () => {
      reviewing(false)
      pending = null
    },
    tierLabel: (tier) => tierLabels[tier as keyof typeof tierLabels] ?? tier,
  }
}

const saveReviewTemplate = html`<div>
  <Panel class="form-panel save-review" r-if="flow.reviewing">
    <h2>Review changes</h2>
    <ul class="change-list">
      <li r-for="change in flow.changes">
        <span>{{ change.label }}</span>
        <span class="setting-tier" :class="'tier-' + change.tier">{{ flow.tierLabel(change.tier) }}</span>
      </li>
    </ul>
    <FormStatus tone="info" r-if="flow.reopens">
      The engine closes and reopens to apply these changes. Reports and tracking pause for a few seconds.
    </FormStatus>
    <FormStatus tone="danger" r-for="warning in flow.warnings">{{ warning }}</FormStatus>
    <div class="form-actions">
      <Btn class="button primary" :tone="flow.warnings.length ? 'danger' : 'accent'" :disabled="flow.busy" @click="flow.confirm">
        {{ flow.busy ? 'Applying…' : flow.warnings.length ? 'Delete data and save' : 'Save changes' }}
      </Btn>
      <Btn class="button" variant="outline" :disabled="flow.busy" @click="flow.cancel">Keep editing</Btn>
    </div>
  </Panel>
  <FormStatus tone="success" r-if="flow.status">{{ flow.status }}</FormStatus>
  <FormStatus tone="danger" role="alert" r-if="flow.error">{{ flow.error }}</FormStatus>
</div>`

export function defineSaveReview() {
  return defineComponent<{ flow: RefOrValue<SaveFlow> }>(saveReviewTemplate, {
    props: ['flow'],
  })
}
