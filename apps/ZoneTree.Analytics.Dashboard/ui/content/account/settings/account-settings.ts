import { defineComponent, html, sref } from 'regor'
import type { MeResponse } from '../../lib/admin-contracts'
import { mountPage } from '../../lib/page'
import { applyUser, dashboardFetch, loadMe } from '../../lib/session'

const template = html`<div class="page-heading">
  <div>
    <div class="eyebrow">YOUR ACCOUNT</div>
    <h1>Account settings</h1>
    <p>Change how your name appears and the password you sign in with.</p>
  </div>
</div>
<div class="admin-columns">
  <Panel class="form-panel">
    <h2>Profile</h2>
    <AppForm @submit.prevent="saveProfile">
      <FormInputField
        id="profile-name"
        label="Display name"
        :model="displayName"
        placeholder="Your name"/>
      <FormStatus tone="success" r-if="profileStatus"
        >{{ profileStatus }}</FormStatus>
      <FormStatus tone="danger" role="alert" r-if="profileError"
        >{{ profileError }}</FormStatus>
      <div class="form-actions">
        <Btn class="button primary" tone="accent" type="submit"
          >Save profile</Btn>
      </div>
    </AppForm>
  </Panel>
  <Panel class="form-panel">
    <h2>Password</h2>
    <p>Changing your password signs out every other browser.</p>
    <AppForm @submit.prevent="savePassword">
      <FormInputField
        id="password-current"
        label="Current password"
        type="password"
        :model="current"
        :required="true"
        autocomplete="current-password"/>
      <FormInputField
        id="password-new"
        label="New password"
        type="password"
        :model="next"
        :required="true"
        autocomplete="new-password"/>
      <FormInputField
        id="password-confirm"
        label="Confirm new password"
        type="password"
        :model="confirm"
        :required="true"
        autocomplete="new-password"/>
      <FormStatus tone="success" r-if="passwordStatus"
        >{{ passwordStatus }}</FormStatus>
      <FormStatus tone="danger" role="alert" r-if="passwordError"
        >{{ passwordError }}</FormStatus>
      <div class="form-actions">
        <Btn class="button primary" tone="accent" type="submit"
          >Change password</Btn>
      </div>
    </AppForm>
  </Panel>
</div>`

const page = defineComponent(template, {
  context: () => {
    const displayName = sref('')
    const current = sref('')
    const next = sref('')
    const confirm = sref('')
    const profileStatus = sref('')
    const profileError = sref('')
    const passwordStatus = sref('')
    const passwordError = sref('')
    void loadMe()
      .then((me) => displayName(me.user.displayName))
      .catch((failure: Error) => profileError(failure.message))
    return {
      displayName,
      current,
      next,
      confirm,
      profileStatus,
      profileError,
      passwordStatus,
      passwordError,
      saveProfile: async () => {
        profileStatus('')
        profileError('')
        try {
          const me = await dashboardFetch<MeResponse>('/auth/me/profile', {
            method: 'PUT',
            body: { displayName: displayName() },
          })
          applyUser(me.user)
          displayName(me.user.displayName)
          profileStatus('Profile saved.')
        } catch (failure) {
          profileError((failure as Error).message)
        }
      },
      savePassword: async () => {
        passwordStatus('')
        passwordError('')
        if (next() !== confirm()) {
          passwordError('The new passwords do not match.')
          return
        }
        try {
          await dashboardFetch<MeResponse>('/auth/me/password', {
            body: { currentPassword: current(), newPassword: next() },
          })
          current('')
          next('')
          confirm('')
          passwordStatus('Password changed. Other browsers are signed out.')
        } catch (failure) {
          passwordError((failure as Error).message)
        }
      },
    }
  },
})

mountPage({ selector: '#account-settings-app', component: page })
