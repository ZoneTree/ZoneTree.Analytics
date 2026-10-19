import { defineComponent, html, sref } from 'regor'
import type { MeResponse } from '../lib/admin-contracts'
import { mountPage } from '../lib/page'
import { applyUser, dashboardFetch, safeReturnUrl } from '../lib/session'

const template = html`<div class="auth-card">
  <div class="auth-heading">
    <div class="eyebrow">WELCOME BACK</div>
    <h1>Sign in</h1>
    <p>Use the account your administrator created for you.</p>
  </div>
  <AppForm class="auth-form" @submit.prevent="submit">
    <FormInputField
      id="signin-user"
      label="User name"
      :model="userName"
      :required="true"
      autocomplete="username"/>
    <FormInputField
      id="signin-password"
      label="Password"
      type="password"
      :model="password"
      :required="true"
      autocomplete="current-password"/>
    <FormStatus tone="danger" role="alert" r-if="error">{{ error }}</FormStatus>
    <Btn class="button primary auth-submit" tone="accent" type="submit" :disabled="busy">
      {{ busy ? 'Signing in…' : 'Sign in' }}
    </Btn>
  </AppForm>
  <p class="auth-footnote">Forgot your password? Ask the administrator to set a new one.</p>
</div>`

const page = defineComponent(template, {
  context: () => {
    const userName = sref('')
    const password = sref('')
    const error = sref('')
    const busy = sref(false)
    const returnUrl = safeReturnUrl(
      new URLSearchParams(location.search).get('returnUrl'),
    )
    return {
      userName,
      password,
      error,
      busy,
      submit: async () => {
        error('')
        busy(true)
        try {
          const me = await dashboardFetch<MeResponse>('/auth/signin', {
            body: { userName: userName(), password: password() },
            redirectOnUnauthorized: false,
          })
          applyUser(me.user)
          location.replace(returnUrl)
        } catch (failure) {
          error((failure as Error).message)
          password('')
        } finally {
          busy(false)
        }
      },
    }
  },
})

mountPage({ selector: '#signin-app', component: page })
