import { computed, defineComponent, html, sref } from 'regor'
import { defineSettingsFieldComponents } from '../components/settings-fields'
import {
  createSitesEditor,
  defineSitesEditor,
  trackingSnippet,
} from '../components/sites-editor'
import type {
  DashboardSettings,
  MeResponse,
  SettingsPreset,
  SettingsSchema,
} from '../lib/admin-contracts'
import { mountPage } from '../lib/page'
import { applyUser, dashboardFetch } from '../lib/session'
import {
  applyPreset,
  browserTimeZone,
  type FieldState,
  presetMatches,
  SettingsFieldError,
  SettingsForm,
  timeZones,
} from '../lib/settings-model'

const steps = [
  'Setup key',
  'Administrator',
  'Sites',
  'Region',
  'Collection',
  'Review',
]

const template = html`<div class="setup-wizard">
  <ol class="setup-steps" aria-label="Setup progress" r-if="!done">
    <li
      r-for="(name, index) in steps"
      :class="{ current: index === step, complete: index < step }"
    >
      <span class="setup-step-number">{{ index + 1 }}</span><span>{{ name }}</span>
    </li>
  </ol>

  <section class="auth-card setup-card" r-if="step === 0 && !done">
    <div class="auth-heading">
      <div class="eyebrow">WELCOME TO ZONETREE ANALYTICS</div>
      <h1>Let's set up your server</h1>
      <p>
        A few choices and you're collecting analytics. Everything except the
        time zone and the storage location can change later in the admin panel.
      </p>
    </div>
    <AppForm class="auth-form" @submit.prevent="verify">
      <FormInputField
        id="setup-key"
        label="Setup key"
        :model="key"
        :required="true"
        placeholder="XXXX-XXXX-XXXX"
        autocomplete="off"/>
      <p class="field-hint">
        The server prints the key in its log when it starts: look for "Setup
        key". Only someone who can read the log can set up this server.
      </p>
      <FormStatus tone="danger" role="alert" r-if="error"
        >{{ error }}</FormStatus>
      <div class="form-actions">
        <Btn
          class="button primary"
          tone="accent"
          type="submit"
          :disabled="busy"
          iconEnd="tabler:arrow-right"
        >
          Continue
        </Btn>
      </div>
    </AppForm>
  </section>

  <section class="auth-card setup-card" r-if="step === 1 && !done">
    <div class="auth-heading">
      <h1>Create the administrator</h1>
      <p>
        The administrator manages settings, sites and users. Other people get
        their own accounts later.
      </p>
    </div>
    <AppForm class="auth-form" @submit.prevent="next">
      <div class="form-grid">
        <FormInputField
          id="admin-user"
          label="User name"
          :model="adminUser"
          :required="true"
          autocomplete="username"/>
        <FormInputField
          id="admin-name"
          label="Display name"
          :model="adminName"
          placeholder="Optional"/>
        <FormInputField
          id="admin-password"
          label="Password"
          type="password"
          :model="adminPassword"
          :required="true"
          autocomplete="new-password"/>
        <FormInputField
          id="admin-confirm"
          label="Confirm password"
          type="password"
          :model="adminConfirm"
          :required="true"
          autocomplete="new-password"/>
      </div>
      <p class="field-hint">
        At least 10 characters. A passphrase of a few words is easy to remember
        and hard to guess.
      </p>
      <FormStatus tone="danger" role="alert" r-if="error"
        >{{ error }}</FormStatus>
      <div class="form-actions">
        <Btn
          class="button"
          variant="outline"
          icon="tabler:arrow-left"
          @click="back"
          >Back</Btn>
        <Btn
          class="button primary"
          tone="accent"
          type="submit"
          iconEnd="tabler:arrow-right"
          >Continue</Btn>
      </div>
    </AppForm>
  </section>

  <section class="auth-card setup-card" r-if="step === 2 && !done">
    <div class="auth-heading">
      <h1>Which websites will you track?</h1>
      <p>
        Add each website and the domains its pages run on. You can add more
        sites later.
      </p>
    </div>
    <AppForm class="auth-form" @submit.prevent="next">
      <SitesEditor :editor="sites"/>
      <FormStatus tone="danger" role="alert" r-if="error"
        >{{ error }}</FormStatus>
      <div class="form-actions">
        <Btn
          class="button"
          variant="outline"
          icon="tabler:arrow-left"
          @click="back"
          >Back</Btn>
        <Btn
          class="button primary"
          tone="accent"
          type="submit"
          iconEnd="tabler:arrow-right"
          >Continue</Btn>
      </div>
    </AppForm>
  </section>

  <section class="auth-card setup-card" r-if="step === 3 && !done">
    <div class="auth-heading">
      <h1>Region and calendar</h1>
      <p>Reports count days, weeks and months in this time zone.</p>
    </div>
    <AppForm class="auth-form" @submit.prevent="next">
      <div class="settings-grid">
        <SettingsField r-for="state in regionFields" :state="state"/>
      </div>
      <FormStatus tone="warning">
        The time zone is stored with your data and cannot change after setup.
      </FormStatus>
      <FormStatus tone="danger" role="alert" r-if="error"
        >{{ error }}</FormStatus>
      <div class="form-actions">
        <Btn
          class="button"
          variant="outline"
          icon="tabler:arrow-left"
          @click="back"
          >Back</Btn>
        <Btn
          class="button primary"
          tone="accent"
          type="submit"
          iconEnd="tabler:arrow-right"
          >Continue</Btn>
      </div>
    </AppForm>
  </section>

  <section class="auth-card setup-card" r-if="step === 4 && !done">
    <div class="auth-heading">
      <h1>What should be collected?</h1>
      <p>
        Pick a starting point. Each choice is a set of privacy and retention
        settings you can fine-tune later.
      </p>
    </div>
    <AppForm class="auth-form" @submit.prevent="next">
      <div class="preset-grid" role="radiogroup" aria-label="Collection">
        <button
          type="button"
          class="preset-card"
          r-for="preset in collectionPresets"
          :class="{ selected: preset.key === collection }"
          role="radio"
          :aria-checked="preset.key === collection ? 'true' : 'false'"
          @click="choose(preset)"
        >
          <strong>{{ preset.label }}</strong>
          <span>{{ preset.description }}</span>
        </button>
      </div>
      <h3 class="settings-group">Filtered reports</h3>
      <div
        class="preset-grid preset-grid-two"
        role="radiogroup"
        aria-label="Indexes"
      >
        <button
          type="button"
          class="preset-card"
          r-for="preset in indexPresets"
          :class="{ selected: preset.key === indexes }"
          role="radio"
          :aria-checked="preset.key === indexes ? 'true' : 'false'"
          @click="choose(preset)"
        >
          <strong>{{ preset.label }}</strong>
          <span>{{ preset.description }}</span>
        </button>
      </div>
      <div class="settings-grid">
        <SettingsField r-for="state in collectionFields" :state="state"/>
      </div>
      <FormStatus tone="danger" role="alert" r-if="error"
        >{{ error }}</FormStatus>
      <div class="form-actions">
        <Btn
          class="button"
          variant="outline"
          icon="tabler:arrow-left"
          @click="back"
          >Back</Btn>
        <Btn
          class="button primary"
          tone="accent"
          type="submit"
          iconEnd="tabler:arrow-right"
          >Continue</Btn>
      </div>
    </AppForm>
  </section>

  <section class="auth-card setup-card" r-if="step === 5 && !done">
    <div class="auth-heading">
      <h1>Review and finish</h1>
      <p>
        Everything below has a sensible default. Open the advanced settings only
        if you need to tune something.
      </p>
    </div>
    <dl class="setup-summary">
      <div>
        <dt>Administrator</dt>
        <dd>{{ adminUser }}</dd>
      </div>
      <div>
        <dt>Sites</dt>
        <dd>{{ siteSummary }}</dd>
      </div>
      <div>
        <dt>Time zone</dt>
        <dd>{{ timeZone }}</dd>
      </div>
      <div>
        <dt>Collection</dt>
        <dd>{{ collectionLabel }}</dd>
      </div>
      <div>
        <dt>Filtered reports</dt>
        <dd>{{ indexLabel }}</dd>
      </div>
    </dl>
    <details class="advanced-settings" @toggle="advancedToggled">
      <summary>Advanced settings</summary>
      <p class="field-hint">
        Settings marked "Chosen during setup" cannot change later.
      </p>
      <div class="settings-section" r-for="section in advancedSections">
        <h2>{{ section.label }}</h2>
        <p class="settings-help">{{ section.help }}</p>
        <SettingsSection
          :form="form"
          :section="section"
          :advanced="true"
          :showTiers="true"/>
      </div>
    </details>
    <FormStatus tone="danger" role="alert" r-if="error"
      >{{ error }}</FormStatus>
    <div class="form-actions">
      <Btn
        class="button"
        variant="outline"
        icon="tabler:arrow-left"
        @click="back"
        :disabled="busy"
        >Back</Btn>
      <Btn
        class="button primary"
        tone="accent"
        @click="finish"
        :disabled="busy"
        icon="tabler:check"
      >
        {{ busy ? 'Setting up…' : 'Finish setup' }}
      </Btn>
    </div>
  </section>

  <section class="auth-card setup-card" r-if="done">
    <div class="auth-heading">
      <div class="eyebrow">ALL SET</div>
      <h1>Your analytics server is ready</h1>
      <p>
        Add this script to every page of your sites. Page views appear in
        reports within seconds.
      </p>
    </div>
    <div class="snippet">
      <code>{{ snippet }}</code>
      <Btn class="button" variant="outline" icon="tabler:copy" @click="copy"
        >{{ copied ? 'Copied' : 'Copy' }}</Btn>
    </div>
    <p class="field-hint">
      Record custom events with zta('signup', { plan: 'pro' }).
    </p>
    <div class="form-actions">
      <a class="button primary" href="/">Open the dashboard</a>
      <a class="button" href="/admin/users/">Invite people</a>
    </div>
  </section>
</div>`

interface SetupDefaults {
  settings: DashboardSettings
  schema: SettingsSchema
}

const page = defineComponent(template, {
  context: () => {
    const step = sref(0)
    const done = sref(false)
    const busy = sref(false)
    const error = sref('')
    const key = sref('')
    const adminUser = sref('')
    const adminName = sref('')
    const adminPassword = sref('')
    const adminConfirm = sref('')
    const copied = sref(false)
    const collection = sref('standard')
    const indexes = sref('dashboard-filters')
    const schema = sref<SettingsSchema | null>(null)
    const form = sref<SettingsForm | null>(null)
    const sites = createSitesEditor([], '', 'PrimarySite', false)
    const siteSummary = sref('')
    const timeZone = sref('')

    const presets = computed(() => schema()?.presets ?? [])
    const field = (key: string): FieldState | undefined =>
      form()?.states.find((state) => state.field.key === key)
    const fields = (...keys: string[]) =>
      keys.map(field).filter((state): state is FieldState => !!state)

    function load(defaults: SetupDefaults) {
      const settings = defaults.settings
      const zone = browserTimeZone()
      if (timeZones().includes(zone)) settings.general.timeZone = zone
      for (const preset of defaults.schema.presets)
        if (preset.key === collection() || preset.key === indexes())
          applyPreset(settings, preset)
      schema(defaults.schema)
      const sections = defaults.schema.sections.filter(
        (section) => section.key !== 'sites',
      )
      form(new SettingsForm(settings, sections, false))
      sites.load(
        settings.sites.items,
        settings.sites.primarySite,
        settings.sites.unmatchedTraffic,
      )
    }

    function collect(): DashboardSettings {
      const current = form() as SettingsForm
      const settings = current.collect()
      Object.assign(settings.sites, sites.collect())
      return settings
    }

    function validateStep(): string {
      switch (step()) {
        case 1:
          if (adminPassword() !== adminConfirm())
            return 'The passwords do not match.'
          if (adminPassword().length < 10)
            return 'Use at least 10 characters for the password.'
          if (!/^[A-Za-z0-9][A-Za-z0-9._@-]{2,63}$/.test(adminUser().trim()))
            return 'Use 3 to 64 letters, digits, dots, dashes, underscores or @ for the user name.'
          return ''
        case 2:
          sites.collect()
          return ''
        default:
          collect()
          return ''
      }
    }

    const verify = async () => {
      error('')
      busy(true)
      try {
        load(
          await dashboardFetch<SetupDefaults>('/setup/verify', {
            body: { key: key() },
            redirectOnUnauthorized: false,
          }),
        )
        step(1)
      } catch (failure) {
        error((failure as Error).message)
      } finally {
        busy(false)
      }
    }

    // The setup link printed by the server carries the key: use it, and keep it out of history.
    const linked = new URLSearchParams(location.search).get('key')
    if (linked) {
      history.replaceState(null, '', location.pathname)
      key(linked)
      void verify()
    }

    return {
      steps,
      step,
      done,
      busy,
      error,
      key,
      adminUser,
      adminName,
      adminPassword,
      adminConfirm,
      copied,
      collection,
      indexes,
      form,
      sites,
      siteSummary,
      timeZone,
      collectionPresets: computed(() =>
        presets().filter((preset) => preset.group === 'collection'),
      ),
      indexPresets: computed(() =>
        presets().filter((preset) => preset.group === 'indexes'),
      ),
      regionFields: computed(() =>
        form()
          ? fields(
              'general.timeZone',
              'general.firstDayOfWeek',
              'general.refreshSeconds',
            )
          : [],
      ),
      collectionFields: computed(() =>
        form()
          ? fields(
              'tracking.bots',
              'sessions.enabled',
              'storage.storeRawEvents',
            )
          : [],
      ),
      advancedSections: computed(() =>
        (schema()?.sections ?? []).filter((section) => section.key !== 'sites'),
      ),
      collectionLabel: computed(
        () =>
          presets().find((preset) => preset.key === collection())?.label ??
          'Custom',
      ),
      indexLabel: computed(
        () =>
          presets().find((preset) => preset.key === indexes())?.label ??
          'Custom',
      ),
      snippet: trackingSnippet(location.origin),
      verify,
      next: () => {
        error('')
        try {
          const problem = validateStep()
          if (problem) {
            error(problem)
            return
          }
          if (step() === 4) {
            const settings = collect()
            const items = settings.sites.items
            siteSummary(items.map((site) => site.name).join(', '))
            timeZone(settings.general.timeZone)
          }
          step(step() + 1)
          window.scrollTo({ top: 0 })
        } catch (failure) {
          error((failure as Error).message)
        }
      },
      back: () => {
        error('')
        step(Math.max(0, step() - 1))
      },
      choose: (preset: SettingsPreset) => {
        const current = form()
        if (!current) return
        const settings = current.collect()
        applyPreset(settings, preset)
        current.refresh()
        if (preset.group === 'collection') collection(preset.key)
        else indexes(preset.key)
      },
      advancedToggled: () => {
        const settings = form()?.document
        if (!settings) return
        collection(
          presets().find(
            (preset) =>
              preset.group === 'collection' && presetMatches(settings, preset),
          )?.key ?? '',
        )
        indexes(
          presets().find(
            (preset) =>
              preset.group === 'indexes' && presetMatches(settings, preset),
          )?.key ?? '',
        )
      },
      finish: async () => {
        error('')
        busy(true)
        try {
          const settings = collect()
          const me = await dashboardFetch<MeResponse>('/setup/complete', {
            body: {
              key: key(),
              administrator: {
                userName: adminUser().trim(),
                displayName: adminName().trim(),
                password: adminPassword(),
              },
              settings,
            },
            redirectOnUnauthorized: false,
          })
          applyUser(me.user)
          done(true)
        } catch (failure) {
          const message =
            failure instanceof SettingsFieldError
              ? failure.message
              : (failure as Error).message
          error(message)
        } finally {
          busy(false)
        }
      },
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
  selector: '#setup-app',
  component: page,
  components: {
    ...defineSettingsFieldComponents(),
    SitesEditor: defineSitesEditor(),
  },
})
