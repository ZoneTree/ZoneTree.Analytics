import { computed, defineComponent, html, sref } from 'regor'
import { defineSettingsFieldComponents } from '../../components/settings-fields'
import { createSaveFlow, defineSaveReview, loadAdmin } from '../../lib/admin'
import type {
  DashboardSettings,
  SettingSection,
  SettingsPreset,
  SettingsSchema,
} from '../../lib/admin-contracts'
import { mountPage } from '../../lib/page'
import {
  applyPreset,
  cloneSettings,
  presetMatches,
  SettingsForm,
} from '../../lib/settings-model'

// Sites and indexes have their own pages.
const excluded = new Set(['sites', 'indexes'])

const template = html`<div class="page-heading">
  <div>
    <div class="eyebrow">ADMINISTRATION</div>
    <h1>Settings</h1>
    <p>Every option of the analytics engine and this dashboard. Changes are reviewed before they apply.</p>
  </div>
</div>
<FormStatus tone="danger" role="alert" r-if="error">{{ error }}</FormStatus>
<FormStatus tone="warning" r-if="restartRequired">
  A saved setting applies after the server restarts: the number of concurrent reports.
</FormStatus>
<div class="settings-layout" r-if="loaded">
  <nav class="settings-nav" aria-label="Settings sections">
    <button type="button" r-for="section in sections" :class="{ active: section.key === active }"
      :aria-current="section.key === active ? 'true' : undefined" @click="select(section)">
      {{ section.label }}
    </button>
  </nav>
  <div class="settings-body">
    <Panel class="form-panel" r-for="section in sections" r-show="section.key === active">
      <h2>{{ section.label }}</h2>
      <p>{{ section.help }}</p>
      <div class="preset-grid" r-if="section.key === 'privacy' || section.key === 'retention'" role="radiogroup" aria-label="Collection presets">
        <button type="button" class="preset-card" r-for="preset in presets"
          :class="{ selected: preset.key === selectedPreset }" role="radio"
          :aria-checked="preset.key === selectedPreset ? 'true' : 'false'" @click="choose(preset)">
          <strong>{{ preset.label }}</strong>
          <span>{{ preset.description }}</span>
        </button>
      </div>
      <label class="check advanced-toggle">
        <input type="checkbox" r-model="advanced"/><span>Show advanced settings</span>
      </label>
      <SettingsSection :form="form" :section="section" :advanced="advanced" :showTiers="true"/>
    </Panel>
    <div class="sticky-actions">
      <Btn class="button primary" tone="accent" @click="save" :disabled="flow.busy || flow.reviewing">Review changes</Btn>
      <Btn class="button" variant="outline" @click="discard" :disabled="flow.busy">Discard edits</Btn>
    </div>
    <SaveReview :flow="flow"/>
  </div>
</div>`

const page = defineComponent(template, {
  context: () => {
    const loaded = sref(false)
    const error = sref('')
    const advanced = sref(false)
    const restartRequired = sref(false)
    const schema = sref<SettingsSchema | null>(null)
    const form = sref<SettingsForm | null>(null)
    const active = sref(location.hash.slice(1) || 'general')
    const selectedPreset = sref('')
    let saved: DashboardSettings | null = null

    const sections = computed(() =>
      (schema()?.sections ?? []).filter(
        (section) => !excluded.has(section.key),
      ),
    )
    const presets = computed(() =>
      (schema()?.presets ?? []).filter(
        (preset) => preset.group === 'collection',
      ),
    )
    const show = (settings: DashboardSettings) => {
      saved = settings
      const document = cloneSettings(settings)
      form(new SettingsForm(document, sections(), true))
      selectedPreset(
        presets().find((preset) => presetMatches(document, preset))?.key ?? '',
      )
      loaded(true)
    }
    const flow = createSaveFlow((next, result) => {
      restartRequired(result.restartRequired)
      show(next)
    })
    void loadAdmin()
      .then((context) => {
        schema(context.schema)
        restartRequired(context.restartRequired)
        if (!sections().some((section) => section.key === active()))
          active('general')
        show(context.settings)
      })
      .catch((failure: Error) => error(failure.message))

    return {
      loaded,
      error,
      advanced,
      restartRequired,
      form,
      active,
      sections,
      presets,
      selectedPreset,
      flow,
      select: (section: SettingSection) => {
        active(section.key)
        history.replaceState(null, '', `#${section.key}`)
      },
      choose: (preset: SettingsPreset) => {
        const current = form() as SettingsForm
        applyPreset(current.collect(), preset)
        current.refresh()
        selectedPreset(preset.key)
      },
      save: () => flow.review(() => (form() as SettingsForm).collect()),
      discard: () => {
        if (saved) show(saved)
        flow.cancel()
      },
    }
  },
})

mountPage({
  selector: '#admin-settings-app',
  component: page,
  components: {
    ...defineSettingsFieldComponents(),
    SaveReview: defineSaveReview(),
  },
})
