import { computed, defineComponent, html, type SRef, sref } from 'regor'
import { defineSettingsFieldComponents } from '../../components/settings-fields'
import { createSaveFlow, defineSaveReview, loadAdmin } from '../../lib/admin'
import type {
  AdminStatus,
  DashboardSettings,
  SettingsPreset,
  SettingsSchema,
} from '../../lib/admin-contracts'
import { mountPage } from '../../lib/page'
import { dashboardFetch } from '../../lib/session'
import {
  applyPreset,
  cloneSettings,
  presetMatches,
  SettingsForm,
  splitList,
} from '../../lib/settings-model'

interface RollupIndexRow {
  dimension: SRef<string>
  groups: SRef<string>
}
interface RollupRow {
  dimensions: SRef<string>
  events: SRef<string>
}
interface ValueRow {
  value: SRef<string>
  percentiles: SRef<boolean>
  firstAndLast: SRef<boolean>
}

const template = html`<div class="page-heading">
  <div>
    <div class="eyebrow">ADMINISTRATION</div>
    <h1>Indexes and rollups</h1>
    <p>Pre-aggregate the reports you read most, and index the filters you use most.</p>
  </div>
</div>
<FormStatus tone="danger" role="alert" r-if="error">{{ error }}</FormStatus>
<FormStatus tone="info" r-if="building">
  Building in the background: {{ building }}. Reports use an index once it is complete.
</FormStatus>
<template r-if="loaded">
  <Panel class="form-panel">
    <h2>Secondary indexes</h2>
    <p>
      Indexes make filtered reports fast, for example visitors from one country by page. Each costs
      storage: a dimension index keeps a full copy of every raw event that has the dimension.
      New indexes are built from retained data in the background.
    </p>
    <div class="preset-grid preset-grid-two" role="radiogroup" aria-label="Index presets">
      <button type="button" class="preset-card" r-for="preset in presets"
        :class="{ selected: preset.key === selectedPreset }" role="radio"
        :aria-checked="preset.key === selectedPreset ? 'true' : 'false'" @click="choose(preset)">
        <strong>{{ preset.label }}</strong>
        <span>{{ preset.description }}</span>
      </button>
    </div>
    <label>
      Dimension indexes
      <input type="text" r-model="dimensionIndexes" placeholder="source, country, device, page"/>
      <span class="field-hint">Ordered copies of raw events by dimension value. Requires raw event storage.</span>
    </label>
    <h3 class="settings-group">Rollup indexes</h3>
    <p class="field-hint">Each row indexes a joint rollup by its first dimension, so a filter on it reads only the matching rows.</p>
    <div class="row-editor">
      <div class="row-editor-row" r-for="row in rollupIndexes">
        <label>Filter dimension<input type="text" r-model="row.dimension" placeholder="country"/></label>
        <label>Grouped by<input type="text" r-model="row.groups" placeholder="page"/></label>
        <Btn class="icon-button" variant="none" icon="tabler:trash" aria-label="Remove index" @click="removeIndex(row)"/>
      </div>
    </div>
    <Btn class="button" variant="outline" icon="tabler:plus" @click="addIndex">Add rollup index</Btn>
  </Panel>

  <Panel class="form-panel">
    <h2>Rollups</h2>
    <p>
      A report is answered from rollups when its group-by and filter dimensions equal a rollup's
      dimensions; otherwise it scans raw events. A rollup added later covers data from then on.
    </p>
    <div class="row-editor">
      <div class="row-editor-row" r-for="row in rollups">
        <label>Dimensions<input type="text" r-model="row.dimensions" placeholder="Empty for totals"/></label>
        <label>Only these events<input type="text" r-model="row.events" placeholder="All events"/></label>
        <Btn class="icon-button" variant="none" icon="tabler:trash" aria-label="Remove rollup" @click="removeRollup(row)"/>
      </div>
    </div>
    <Btn class="button" variant="outline" icon="tabler:plus" @click="addRollup">Add rollup</Btn>
    <h3 class="settings-group">Value aggregates</h3>
    <p class="field-hint">Every tracked value keeps sum, count, minimum, maximum, average and variance. Percentiles and first/last values are opt-in.</p>
    <div class="row-editor">
      <div class="row-editor-row" r-for="row in values">
        <label>Value name<input type="text" r-model="row.value" placeholder="load_time"/></label>
        <div class="setting-choices">
          <label class="check"><input type="checkbox" r-model="row.percentiles"/><span>Percentiles</span></label>
          <label class="check"><input type="checkbox" r-model="row.firstAndLast"/><span>First and last</span></label>
        </div>
        <Btn class="icon-button" variant="none" icon="tabler:trash" aria-label="Remove value" @click="removeValue(row)"/>
      </div>
    </div>
    <Btn class="button" variant="outline" icon="tabler:plus" @click="addValue">Add value</Btn>
    <h3 class="settings-group">Rollup options</h3>
    <label class="check advanced-toggle">
      <input type="checkbox" r-model="advanced"/><span>Show advanced settings</span>
    </label>
    <SettingsSection r-for="section in sections" :form="form" :section="section" :advanced="advanced" :showTiers="true"/>
  </Panel>
  <div class="sticky-actions">
    <Btn class="button primary" tone="accent" @click="save" :disabled="flow.busy || flow.reviewing">Review changes</Btn>
  </div>
  <SaveReview :flow="flow"/>
</template>`

const page = defineComponent(template, {
  context: () => {
    const loaded = sref(false)
    const error = sref('')
    const advanced = sref(false)
    const building = sref('')
    const schema = sref<SettingsSchema | null>(null)
    const form = sref<SettingsForm | null>(null)
    const dimensionIndexes = sref('')
    const rollupIndexes = sref<RollupIndexRow[]>([])
    const rollups = sref<RollupRow[]>([])
    const values = sref<ValueRow[]>([])
    const selectedPreset = sref('')
    let timer = 0

    const indexRow = (
      dimension = '',
      groups: string[] = [],
    ): RollupIndexRow => ({
      dimension: sref(dimension),
      groups: sref(groups.join(', ')),
    })
    const rollupRow = (
      dimensions: string[] = [],
      events: string[] | null = null,
    ): RollupRow => ({
      dimensions: sref(dimensions.join(', ')),
      events: sref((events ?? []).join(', ')),
    })
    const valueRow = (
      value = '',
      percentiles = false,
      firstAndLast = false,
    ): ValueRow => ({
      value: sref(value),
      percentiles: sref(percentiles),
      firstAndLast: sref(firstAndLast),
    })
    const presets = computed(() =>
      (schema()?.presets ?? []).filter((preset) => preset.group === 'indexes'),
    )

    function show(settings: DashboardSettings) {
      const current = cloneSettings(settings)
      form(
        new SettingsForm(
          current,
          (schema()?.sections ?? []).filter(
            (s) => s.key === 'rollups' || s.key === 'indexes',
          ),
          true,
        ),
      )
      dimensionIndexes(current.indexes.dimensions.join(', '))
      rollupIndexes(
        current.indexes.rollups.map((index) =>
          indexRow(index.dimension, index.groupDimensions),
        ),
      )
      rollups(
        current.rollups.definitions.map((definition) =>
          rollupRow(definition.dimensions, definition.events),
        ),
      )
      values(
        current.rollups.valueAggregates.map((value) =>
          valueRow(value.value, value.percentiles, value.firstAndLast),
        ),
      )
      selectedPreset(
        presets().find((preset) => presetMatches(current, preset))?.key ?? '',
      )
      loaded(true)
    }

    function collect(): DashboardSettings {
      const settings = (form() as SettingsForm).collect()
      settings.indexes.dimensions = splitList(dimensionIndexes())
      settings.indexes.rollups = rollupIndexes()
        .filter((row) => row.dimension().trim())
        .map((row) => ({
          dimension: row.dimension().trim(),
          groupDimensions: splitList(row.groups()),
        }))
      settings.rollups.definitions = rollups().map((row) => {
        const events = splitList(row.events())
        return {
          dimensions: splitList(row.dimensions()),
          events: events.length ? events : null,
        }
      })
      settings.rollups.valueAggregates = values()
        .filter((row) => row.value().trim())
        .map((row) => ({
          value: row.value().trim(),
          percentiles: row.percentiles(),
          firstAndLast: row.firstAndLast(),
        }))
      return settings
    }

    const watchBuild = async () => {
      try {
        const status = await dashboardFetch<AdminStatus>('/admin/status')
        building(status.engine.indexMaintenance ?? '')
        window.clearTimeout(timer)
        if (
          status.engine.indexMaintenance ||
          status.engine.state === 'Reopening'
        )
          timer = window.setTimeout(watchBuild, 2000)
      } catch {
        building('')
      }
    }
    window.addEventListener('pagehide', () => window.clearTimeout(timer), {
      once: true,
    })

    const flow = createSaveFlow((saved) => {
      show(saved)
      void watchBuild()
    })
    void loadAdmin()
      .then((context) => {
        schema(context.schema)
        show(context.settings)
        void watchBuild()
      })
      .catch((failure: Error) => error(failure.message))

    return {
      loaded,
      error,
      advanced,
      building,
      form,
      dimensionIndexes,
      rollupIndexes,
      rollups,
      values,
      presets,
      selectedPreset,
      flow,
      sections: computed(() =>
        (schema()?.sections ?? []).filter(
          (section) => section.key === 'rollups' || section.key === 'indexes',
        ),
      ),
      choose: (preset: SettingsPreset) => {
        const settings = collect()
        applyPreset(settings, preset)
        dimensionIndexes(settings.indexes.dimensions.join(', '))
        rollupIndexes(
          settings.indexes.rollups.map((index) =>
            indexRow(index.dimension, index.groupDimensions),
          ),
        )
        selectedPreset(preset.key)
      },
      addIndex: () => rollupIndexes([...rollupIndexes(), indexRow()]),
      removeIndex: (row: RollupIndexRow) =>
        rollupIndexes(rollupIndexes().filter((item) => item !== row)),
      addRollup: () => rollups([...rollups(), rollupRow()]),
      removeRollup: (row: RollupRow) =>
        rollups(rollups().filter((item) => item !== row)),
      addValue: () => values([...values(), valueRow()]),
      removeValue: (row: ValueRow) =>
        values(values().filter((item) => item !== row)),
      save: () => flow.review(collect),
    }
  },
})

mountPage({
  selector: '#admin-indexes-app',
  component: page,
  components: {
    ...defineSettingsFieldComponents(),
    SaveReview: defineSaveReview(),
  },
})
