import { defineComponent, html, sref } from 'regor'
import { mountPage } from '../lib/page'
import { applyUser, dashboardFetch } from '../lib/session'

const template = html`<div class="auth-card">
  <div class="auth-heading">
    <div class="eyebrow">SIGNED OUT</div>
    <h1>{{ done ? 'You are signed out' : 'Signing out…' }}</h1>
    <p r-if="done">This browser no longer has access to the dashboard.</p>
  </div>
  <FormStatus tone="danger" role="alert" r-if="error"
    >{{ error }}</FormStatus>
  <a class="button primary auth-submit" href="/signin/" r-if="done"
    >Sign in again</a>
</div>`

const page = defineComponent(template, {
  context: () => {
    const done = sref(false)
    const error = sref('')
    void dashboardFetch('/auth/signout', {
      body: {},
      redirectOnUnauthorized: false,
    })
      .catch((failure: Error) => error(failure.message))
      .finally(() => {
        applyUser(null)
        done(true)
      })
    return { done, error }
  },
})

mountPage({ selector: '#signout-app', component: page })
