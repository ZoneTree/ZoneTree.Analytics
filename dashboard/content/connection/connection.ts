import { defineComponent, html, sref } from 'regor'
import { ApiClient } from '../lib/api'
import {
  loadConfig,
  parseSites,
  validateConfig,
  writeStorage,
} from '../lib/config'
import type { Config, SummaryReport } from '../lib/contracts'
import type { DashboardModel } from '../lib/dashboard-model'
import { mountReport } from '../lib/mount'

export function createConnectionModel(model: DashboardModel) {
  const mode = sref(model.config().mode)
  const apiBaseUrl = sref(model.config().apiBaseUrl)
  const timeZone = sref(model.config().timeZone)
  const refreshSeconds = sref<string | number>(model.config().refreshSeconds)
  const sites = sref(
    model
      .config()
      .sites.map((site) => `${site.id} | ${site.name}`)
      .join('\n'),
  )
  const token = sref(model.api.token)
  const status = sref('')
  const testing = sref(false)
  let testController: AbortController | null = null
  model.registerCleanup(() => testController?.abort())
  const candidate = (): Config =>
    validateConfig({
      ...model.config(),
      mode: mode(),
      apiBaseUrl: apiBaseUrl(),
      timeZone: timeZone(),
      refreshSeconds: Number(refreshSeconds()),
      sites: parseSites(sites()),
    })
  function apply(value: Config) {
    mode(value.mode)
    apiBaseUrl(value.apiBaseUrl)
    timeZone(value.timeZone)
    refreshSeconds(value.refreshSeconds)
    sites(value.sites.map((site) => `${site.id} | ${site.name}`).join('\n'))
    model.updateConfig(value)
  }
  return {
    mode,
    apiBaseUrl,
    timeZone,
    refreshSeconds,
    sites,
    token,
    status,
    testing,
    modes: [
      { value: 'demo', label: 'Demo · synthetic data' },
      { value: 'live', label: 'Live reporting API' },
    ],
    refreshOptions: [
      { value: 0, label: 'Off' },
      { value: 30, label: 'Every 30 seconds' },
      { value: 60, label: 'Every minute' },
      { value: 300, label: 'Every 5 minutes' },
    ],
    save: (event: Event) => {
      event.preventDefault()
      model.error('')
      status('')
      try {
        const value = candidate()
        writeStorage('zt-dashboard-connection-v1', value)
        writeStorage(
          'zt-dashboard-token-v1',
          { base: value.apiBaseUrl, token: token().trim() },
          true,
        )
        model.api.token = token().trim()
        apply(value)
        status('Connection saved. Open a report from the sidebar to use it.')
      } catch (failure) {
        model.error((failure as Error).message)
      }
    },
    test: async () => {
      status('')
      model.error('')
      testing(true)
      testController?.abort()
      testController = new AbortController()
      try {
        const value = candidate()
        const client = new ApiClient(value)
        client.token = token().trim()
        const report = await client.request<SummaryReport>(
          'summary',
          {
            site: value.sites[0].id,
            range: 'today',
            compare: 'none',
            tz: value.timeZone,
            filters: [],
          },
          testController.signal,
        )
        status(
          `API connected. Today's report has ${model.number(report.summary.visitors)} visitors for ${value.sites[0].name}.`,
        )
      } catch (failure) {
        model.error((failure as Error).message)
      } finally {
        testing(false)
      }
    },
    reset: async () => {
      try {
        writeStorage('zt-dashboard-connection-v1', null)
        writeStorage('zt-dashboard-token-v1', null, true)
        model.api.token = ''
        token('')
        apply(await loadConfig())
        model.error('')
        status('Restored the configuration supplied by your host.')
      } catch (failure) {
        model.error((failure as Error).message)
      }
    },
  }
}

export interface ConnectionPage {
  dashboard: DashboardModel
  connection: ReturnType<typeof createConnectionModel>
}

const connectionTemplate = html`
  <ReportHeader :dashboard="dashboard" />
  <ReportFeedback :dashboard="dashboard" />
  <Panel class="form-panel">
    <h2>Your analytics connection</h2>
    <p>
      Choose your data source and reporting settings, then test and save your connection.
    </p>
    <AppForm @submit.prevent="connection.save">
      <div class="form-grid">
        <FormSelectField
          id="connection-mode"
          label="Data source"
          :model="connection.mode"
          :options="connection.modes"
        />
        <FormInputField
          id="connection-url"
          label="Reporting API URL"
          :model="connection.apiBaseUrl"
          :required="true"
          placeholder="/_analytics/api"
        />
        <FormInputField
          id="connection-timezone"
          label="Reporting time zone"
          :model="connection.timeZone"
          :required="true"
          placeholder="Europe/Berlin"
        />
        <FormSelectField
          id="connection-refresh"
          label="Auto-refresh"
          :model="connection.refreshSeconds"
          :options="connection.refreshOptions"
        />
        <label class="wide-field" for="connection-sites">
          Sites
          <textarea id="connection-sites" r-model="connection.sites" rows="4" required></textarea>
          <span class="field-hint">
            One site per line: site ID | display name. An empty ID uses the default site.
            The host supplies the initial list; these settings override it in this browser.
          </span>
        </label>
        <div class="wide-field">
          <FormInputField
            id="connection-token"
            label="Bearer token (optional)"
            type="password"
            :model="connection.token"
            autocomplete="off"
            placeholder="Leave blank for host cookie authentication"
          />
          <p class="field-hint">
            Stored in sessionStorage for this tab so it survives page navigation. Cleared when the
            tab closes. Never stored in a URL or localStorage.
          </p>
        </div>
      </div>
      <div class="form-actions">
        <Btn class="button primary" tone="accent" type="submit" :disabled="connection.testing">
          Save connection
        </Btn>
        <Btn
          class="button"
          variant="outline"
          :disabled="connection.testing"
          @click="connection.test"
        >
          Test API connection
        </Btn>
        <Btn
          class="button quiet"
          variant="none"
          :disabled="connection.testing"
          @click="connection.reset"
        >
          Reset settings
        </Btn>
      </div>
      <FormStatus tone="success" r-if="connection.status">{{ connection.status }}</FormStatus>
      <FormStatus tone="info" r-if="connection.testing">Testing the reporting API…</FormStatus>
    </AppForm>
  </Panel>
  <ReportFooter :dashboard="dashboard" />
`

export function defineConnectionPage(model: DashboardModel) {
  return defineComponent<ConnectionPage>(connectionTemplate, {
    context: () => ({
      dashboard: model,
      connection: createConnectionModel(model),
    }),
  })
}

void mountReport({
  kind: 'settings',
  selector: '#connection-app',
  component: defineConnectionPage,
})
