import { type SRef, sref } from 'regor'
import type {
  DashboardSettings,
  SettingField,
  SettingSection,
  SettingsPreset,
  SettingTier,
} from './admin-contracts'

// Field types the generic form renders; the rest have dedicated editors on their own pages.
const genericTypes = new Set([
  'toggle',
  'number',
  'text',
  'select',
  'tags',
  'map',
  'multi',
  'timeZone',
])

export const tierLabels: Record<SettingTier, string> = {
  Live: 'Applies at once',
  Reopen: 'Reopens the engine',
  Restart: 'Needs a server restart',
  Fixed: 'Chosen during setup',
}

export function readPath(document: unknown, key: string): unknown {
  let current = document as Record<string, unknown> | undefined
  for (const segment of key.split('.')) {
    if (current === null || typeof current !== 'object') return undefined
    current = current[segment] as Record<string, unknown> | undefined
  }
  return current
}

export function writePath(document: unknown, key: string, value: unknown) {
  const segments = key.split('.')
  let current = document as Record<string, unknown>
  for (const segment of segments.slice(0, -1)) {
    if (current[segment] === null || typeof current[segment] !== 'object')
      current[segment] = {}
    current = current[segment] as Record<string, unknown>
  }
  current[segments[segments.length - 1]] = value
}

export function cloneSettings<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

export function applyPreset(
  document: DashboardSettings,
  preset: SettingsPreset,
) {
  for (const [key, value] of Object.entries(preset.values))
    writePath(document, key, cloneSettings(value))
}

export function presetMatches(
  document: DashboardSettings,
  preset: SettingsPreset,
) {
  return Object.entries(preset.values).every(
    ([key, value]) =>
      JSON.stringify(readPath(document, key) ?? null) ===
      JSON.stringify(value ?? null),
  )
}

export function splitList(text: string): string[] {
  return [
    ...new Set(
      text
        .split(/[\n,]/)
        .map((item) => item.trim())
        .filter(Boolean),
    ),
  ]
}

export function timeZones(): string[] {
  const supported =
    typeof Intl.supportedValuesOf === 'function'
      ? Intl.supportedValuesOf('timeZone')
      : []
  return ['UTC', ...supported.filter((zone) => zone !== 'UTC')]
}

export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
}

export interface ChoiceState {
  value: string | number
  label: string
  checked: SRef<boolean>
}

export interface FieldState {
  kind: 'field'
  field: SettingField
  id: string
  label: string
  hint: string
  tierLabel: string
  locked: boolean
  text: SRef<string | number>
  checked: SRef<boolean>
  empty: SRef<boolean>
  choices: ChoiceState[]
  options: { value: string | number; label: string }[]
}

export interface GroupHeading {
  kind: 'group'
  label: string
  advanced: boolean
}

export type FormEntry = FieldState | GroupHeading

function hintOf(field: SettingField): string {
  const range =
    field.type === 'number' && field.min !== null && field.max !== null
      ? `${field.min.toLocaleString()}–${field.max.toLocaleString()}${field.unit ? ` ${field.unit}` : ''}.`
      : field.unit
        ? `In ${field.unit}.`
        : ''
  return [field.help, range].filter(Boolean).join(' ')
}

function createState(
  field: SettingField,
  document: DashboardSettings,
  lockFixed: boolean,
): FieldState {
  const value = readPath(document, field.key)
  const empty = value === null || value === undefined
  const options = [...(field.options ?? [])]
  if (field.type === 'select' && field.nullable)
    options.unshift({ value: '', label: field.emptyLabel ?? 'Default' })
  if (field.type === 'timeZone')
    options.push(...timeZones().map((zone) => ({ value: zone, label: zone })))
  const selected = new Set(
    Array.isArray(value) ? (value as unknown[]).map(String) : [],
  )
  let text: string | number = ''
  if (field.type === 'tags') text = ((value as string[]) ?? []).join(', ')
  else if (field.type === 'map')
    text = Object.entries((value as Record<string, string>) ?? {})
      .map(([key, item]) => `${key} = ${item}`)
      .join('\n')
  else if (!empty && field.type !== 'toggle' && field.type !== 'multi')
    text = value as string | number
  return {
    kind: 'field',
    field,
    id: `setting-${field.key.replace(/\./g, '-')}`,
    label: field.label,
    hint: hintOf(field),
    tierLabel: tierLabels[field.tier],
    locked: lockFixed && field.tier === 'Fixed',
    text: sref(text),
    checked: sref(value === true),
    empty: sref(
      empty &&
        field.nullable &&
        field.type !== 'select' &&
        field.type !== 'text',
    ),
    choices: (field.type === 'multi' ? (field.options ?? []) : []).map(
      (option) => ({
        ...option,
        checked: sref(selected.has(String(option.value))),
      }),
    ),
    options,
  }
}

export class SettingsFieldError extends Error {
  constructor(
    message: string,
    public key: string,
  ) {
    super(message)
  }
}

function fieldValue(state: FieldState): unknown {
  const { field } = state
  switch (field.type) {
    case 'toggle':
      return state.empty() ? null : state.checked()
    case 'multi':
      return state.choices
        .filter((choice) => choice.checked())
        .map((choice) => choice.value)
    case 'tags':
      return splitList(String(state.text()))
    case 'map': {
      const map: Record<string, string> = {}
      for (const line of String(state.text()).split('\n')) {
        if (!line.trim()) continue
        const separator = line.indexOf('=')
        if (separator < 1)
          throw new SettingsFieldError(
            `${field.label}: write each entry as ${field.keyLabel ?? 'key'} = ${field.valueLabel ?? 'value'}.`,
            field.key,
          )
        map[line.slice(0, separator).trim()] = line.slice(separator + 1).trim()
      }
      return map
    }
    case 'number': {
      if (state.empty()) return null
      const raw = String(state.text()).trim()
      const number = Number(raw)
      if (!raw || !Number.isFinite(number))
        throw new SettingsFieldError(
          `${field.label} needs a number.`,
          field.key,
        )
      if (
        (field.min !== null && number < field.min) ||
        (field.max !== null && number > field.max)
      )
        throw new SettingsFieldError(
          `${field.label} must be between ${field.min?.toLocaleString()} and ${field.max?.toLocaleString()}.`,
          field.key,
        )
      return number
    }
    case 'select': {
      const selected = state.text()
      if (selected === '' && field.nullable) return null
      const option = state.options.find(
        (item) => String(item.value) === String(selected),
      )
      return option ? option.value : selected
    }
    default: {
      const text = String(state.text()).trim()
      return text === '' && field.nullable ? null : text
    }
  }
}

// Form state for the generic settings of some sections. collect() writes the edited values
// into the document it was created from.
export class SettingsForm {
  readonly states: FieldState[] = []

  constructor(
    public document: DashboardSettings,
    readonly sections: SettingSection[],
    readonly lockFixed: boolean,
  ) {
    this.rebuild()
  }

  entries(section: SettingSection): FormEntry[] {
    const entries: FormEntry[] = []
    const visit = (field: SettingField, advanced: boolean) => {
      if (field.fields) {
        entries.push({
          kind: 'group',
          label: field.label,
          advanced: advanced || field.advanced,
        })
        for (const child of field.fields)
          visit(child, advanced || field.advanced)
        return
      }
      const state = this.states.find((item) => item.field.key === field.key)
      if (state) entries.push(state)
    }
    for (const field of section.fields) visit(field, false)
    return entries
  }

  rebuild() {
    this.states.length = 0
    const visit = (field: SettingField, advanced: boolean) => {
      if (field.fields) {
        for (const child of field.fields)
          visit(
            {
              ...child,
              advanced: advanced || field.advanced || child.advanced,
            },
            advanced || field.advanced,
          )
        return
      }
      if (genericTypes.has(field.type))
        this.states.push(
          createState(
            { ...field, advanced: advanced || field.advanced },
            this.document,
            this.lockFixed,
          ),
        )
    }
    for (const section of this.sections)
      for (const field of section.fields) visit(field, false)
  }

  // Values already in the document refresh the form, for example after a preset.
  refresh() {
    for (const state of this.states) {
      const fresh = createState(state.field, this.document, this.lockFixed)
      state.text(fresh.text())
      state.checked(fresh.checked())
      state.empty(fresh.empty())
      state.choices.forEach((choice, index) => {
        choice.checked(fresh.choices[index].checked())
      })
    }
  }

  collect(): DashboardSettings {
    for (const state of this.states)
      if (!state.locked)
        writePath(this.document, state.field.key, fieldValue(state))
    return this.document
  }
}
