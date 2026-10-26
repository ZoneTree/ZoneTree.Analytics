import { defineComponent, html, sref } from 'regor'
import {
  createSitesEditor,
  defineSitesEditor,
  trackingSnippet,
} from '../../components/sites-editor'
import { createSaveFlow, defineSaveReview, loadAdmin } from '../../lib/admin'
import type { DashboardSettings } from '../../lib/admin-contracts'
import { mountPage } from '../../lib/page'
import { cloneSettings } from '../../lib/settings-model'

const template = html`<div class="page-heading">
  <div>
    <div class="eyebrow">ADMINISTRATION</div>
    <h1>Sites</h1>
    <p>
      The websites this server collects analytics for, and the domains their
      pages run on.
    </p>
  </div>
</div>
<FormStatus tone="danger" role="alert" r-if="error"
  >{{ error }}</FormStatus>
<Panel class="form-panel" r-if="loaded">
  <h2>Websites</h2>
  <p>
    Removing a site hides it from reports and users; its stored data stays until
    retention removes it.
  </p>
  <AppForm @submit.prevent="save">
    <SitesEditor :editor="editor"/>
    <label class="check setting-toggle">
      <input type="checkbox" r-model="trackUsage"/>
      <span
        >Record visits to this dashboard in a separate "Dashboard usage" site
        that only you see</span>
    </label>
    <div class="form-actions">
      <Btn
        class="button primary"
        tone="accent"
        type="submit"
        :disabled="flow.busy || flow.reviewing"
        >Review changes</Btn>
    </div>
  </AppForm>
  <SaveReview :flow="flow"/>
</Panel>
<Panel class="form-panel" r-if="loaded">
  <h2>Tracking snippet</h2>
  <p>
    Add this script to every page of your sites. The server assigns each page
    view to the site whose domain matches.
  </p>
  <div class="snippet">
    <code>{{ snippet }}</code>
    <Btn class="button" variant="outline" icon="tabler:copy" @click="copy"
      >{{ copied ? 'Copied' : 'Copy' }}</Btn>
  </div>
  <p class="field-hint">
    Record custom events with zta('signup', { plan: 'pro' }). Add
    data-hash-routing="true" for hash-based single-page apps.
  </p>
</Panel>`

const page = defineComponent(template, {
  context: () => {
    const editor = createSitesEditor([], '', 'PrimarySite', true)
    const trackUsage = sref(false)
    const loaded = sref(false)
    const error = sref('')
    const copied = sref(false)
    let settings: DashboardSettings | null = null
    const show = (next: DashboardSettings) => {
      settings = next
      editor.load(
        next.sites.items,
        next.sites.primarySite,
        next.sites.unmatchedTraffic,
      )
      trackUsage(next.sites.trackDashboardUsage)
      loaded(true)
    }
    const flow = createSaveFlow((saved) => show(saved))
    void loadAdmin()
      .then((context) => show(context.settings))
      .catch((failure: Error) => error(failure.message))
    return {
      editor,
      trackUsage,
      loaded,
      error,
      copied,
      flow,
      snippet: trackingSnippet(location.origin),
      save: () =>
        flow.review(() => {
          const next = cloneSettings(settings as DashboardSettings)
          Object.assign(next.sites, editor.collect(), {
            trackDashboardUsage: trackUsage(),
          })
          return next
        }),
      copy: async () => {
        try {
          await navigator.clipboard.writeText(trackingSnippet(location.origin))
          copied(true)
        } catch {
          copied(false)
        }
      },
    }
  },
})

mountPage({
  selector: '#admin-sites-app',
  component: page,
  components: {
    SitesEditor: defineSitesEditor(),
    SaveReview: defineSaveReview(),
  },
})
