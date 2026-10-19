import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  type RefOrValue,
  unref,
} from 'regor'
import type { SettingSection } from '../lib/admin-contracts'
import type { FieldState, FormEntry, SettingsForm } from '../lib/settings-model'

export interface SettingsFieldView {
  state: RefOrValue<FieldState>
}

export interface SettingsSectionView {
  form: RefOrValue<SettingsForm>
  section: RefOrValue<SettingSection>
  advanced: RefOrValue<boolean>
  showTiers?: RefOrValue<boolean>
  entries: ComputedRef<FormEntry[]>
}

// One schema-described setting. Values are bound as text and props, never HTML.
const fieldTemplate = html`<div class="setting-field" :class="{ 'setting-locked': state.locked }" :data-setting="state.field.key">
  <div class="setting-label">
    <label :for="state.id" r-if="state.field.type !== 'toggle' && state.field.type !== 'multi'">{{ state.label }}</label>
    <span r-if="state.field.type === 'multi'">{{ state.label }}</span>
    <span class="setting-tier" r-if="showTiers" :class="'tier-' + state.field.tier">{{ state.tierLabel }}</span>
  </div>
  <label class="check setting-toggle" r-if="state.field.type === 'toggle'">
    <input type="checkbox" :id="state.id" r-model="state.checked" :disabled="state.locked || state.empty"/>
    <span>{{ state.label }}</span>
  </label>
  <div class="setting-choices" r-if="state.field.type === 'multi'">
    <label class="check" r-for="choice in state.choices">
      <input type="checkbox" r-model="choice.checked" :disabled="state.locked"/>
      <span>{{ choice.label }}</span>
    </label>
  </div>
  <div class="setting-input" r-if="state.field.type === 'number' || state.field.type === 'text'">
    <input
      :id="state.id"
      :type="state.field.type === 'number' ? 'number' : 'text'"
      r-model="state.text"
      :min="state.field.min"
      :max="state.field.max"
      step="any"
      :disabled="state.locked || state.empty"
      :placeholder="state.empty ? state.field.emptyLabel : ''"/>
    <span class="setting-unit" r-if="state.field.unit">{{ state.field.unit }}</span>
  </div>
  <label class="check setting-empty" r-if="state.field.nullable && (state.field.type === 'number' || state.field.type === 'toggle')">
    <input type="checkbox" r-model="state.empty" :disabled="state.locked"/>
    <span>{{ state.field.emptyLabel || 'Default' }}</span>
  </label>
  <select :id="state.id" r-model="state.text" :disabled="state.locked"
    r-if="state.field.type === 'select' || state.field.type === 'timeZone'">
    <option r-for="option in state.options" :value="option.value">{{ option.label }}</option>
  </select>
  <input :id="state.id" type="text" r-model="state.text" :disabled="state.locked"
    placeholder="Separate with commas" r-if="state.field.type === 'tags'"/>
  <textarea :id="state.id" r-model="state.text" rows="3" :disabled="state.locked"
    :placeholder="(state.field.keyLabel || 'key') + ' = ' + (state.field.valueLabel || 'value')"
    r-if="state.field.type === 'map'"></textarea>
  <span class="field-hint" r-if="state.hint">{{ state.hint }}</span>
</div>`

const sectionTemplate = html`<div class="settings-grid">
  <template r-for="entry in entries">
    <h3 class="settings-group" r-if="entry.kind === 'group'">{{ entry.label }}</h3>
    <SettingsField r-if="entry.kind === 'field'" :state="entry" :showTiers="showTiers"/>
  </template>
</div>`

export function defineSettingsFieldComponents() {
  return {
    SettingsField: defineComponent<
      SettingsFieldView & { showTiers?: RefOrValue<boolean> }
    >(fieldTemplate, {
      props: ['state', 'showTiers'],
    }),
    SettingsSection: defineComponent<SettingsSectionView>(sectionTemplate, {
      props: ['form', 'section', 'advanced', 'showTiers'],
      context: (head) => {
        const props = head.props as SettingsSectionView
        return {
          ...props,
          entries: computed(() => {
            const form = unref(props.form) as SettingsForm
            const advanced = unref(props.advanced)
            return form
              .entries(unref(props.section) as SettingSection)
              .filter(
                (entry) =>
                  advanced ||
                  !(entry.kind === 'group'
                    ? entry.advanced
                    : entry.field.advanced),
              )
          }),
        }
      },
    }),
  }
}
