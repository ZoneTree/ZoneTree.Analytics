import { computed, defineComponent, html, sref } from 'regor'
import type { AdminStatus } from '../lib/admin-contracts'
import { formatDate, mountPage } from '../lib/page'
import { dashboardFetch, loadMe } from '../lib/session'

const stateLabels: Record<string, string> = {
  NotConfigured: 'Not set up',
  Opening: 'Opening',
  Running: 'Running',
  Reopening: 'Applying settings',
  Failed: 'Failed to open',
}

const template = html`<div class="page-heading">
  <div>
    <div class="eyebrow">ADMINISTRATION</div>
    <h1>Server status</h1>
    <p>The analytics engine, background index work and storage.</p>
  </div>
  <div class="heading-actions">
    <Btn
      class="button"
      variant="outline"
      icon="tabler:refresh"
      @click="refresh"
      :disabled="loading"
      >Refresh</Btn>
  </div>
</div>
<FormStatus tone="danger" role="alert" r-if="error"
  >{{ error }}</FormStatus>
<FormStatus tone="warning" r-if="status && status.restartRequired">
  A saved setting applies after the server restarts: the number of concurrent
  reports.
</FormStatus>
<FormStatus tone="danger" r-if="status && status.engine.error">
  The engine could not open: {{ status.engine.error }}
</FormStatus>
<FormStatus
  tone="warning"
  r-if="status && status.engine.ipLocation && status.engine.ipLocation.lastError"
>
  IP location database: {{ status.engine.ipLocation.lastError }}
</FormStatus>
<div class="status-cards" r-if="status">
  <Panel class="status-card">
    <span>Engine</span>
    <strong>{{ stateLabel }}</strong>
    <small>{{ status.engine.openedAt ? 'Since ' + formatDate(status.engine.openedAt) : '—' }}</small>
  </Panel>
  <Panel class="status-card">
    <span>Indexes</span>
    <strong>{{ status.engine.indexMaintenance ? 'Building' : 'Up to date' }}</strong>
    <small>{{ status.engine.indexMaintenance || 'No background work' }}</small>
  </Panel>
  <Panel class="status-card">
    <span>Events processed</span>
    <strong>{{ number(status.engine.eventsProcessed) }}</strong>
    <small
      >{{ number(status.engine.eventsAccepted) }} accepted · {{ number(status.engine.eventsDropped) }} dropped
      since opening</small>
  </Panel>
  <Panel class="status-card">
    <span>Queue</span>
    <strong>{{ number(status.engine.queueLength) }}</strong>
    <small
      >{{ number(status.engine.errors) }} errors · {{ number(status.engine.openPartitions) }} open
      partitions</small>
  </Panel>
</div>
<div class="admin-columns" r-if="status">
  <Panel class="form-panel">
    <h2>Server</h2>
    <dl class="detail-list">
      <div>
        <dt>Sites</dt>
        <dd><a href="/admin/sites/">{{ status.sites }}</a></dd>
      </div>
      <div>
        <dt>Users</dt>
        <dd><a href="/admin/users/">{{ status.users }}</a></dd>
      </div>
      <div>
        <dt>Analytics data</dt>
        <dd><code>{{ status.dataDirectory }}</code></dd>
      </div>
      <div>
        <dt>Users and settings</dt>
        <dd><code>{{ status.systemDirectory }}</code></dd>
      </div>
      <div>
        <dt>IP location</dt>
        <dd r-if="!status.engine.ipLocation">Off</dd>
        <dd r-else-if="!status.engine.ipLocation.databaseType">Downloading…</dd>
        <dd r-else>
          {{ status.engine.ipLocation.databaseType }} · {{ status.engine.ipLocation.precision }} precision ·
          built {{ formatDate(status.engine.ipLocation.buildTime) }}
          <small r-if="status.engine.ipLocation.lastChecked"
            >· checked {{ formatDate(status.engine.ipLocation.lastChecked) }}</small>
        </dd>
      </div>
    </dl>
  </Panel>
  <Panel class="form-panel">
    <h2>Partitions</h2>
    <p>
      ZoneTree partitions of each data tier, with approximate record counts.
    </p>
    <div class="table-wrap" r-if="status.engine.partitions.length">
      <table class="data-table">
        <thead>
          <tr>
            <th scope="col">Partition</th>
            <th scope="col" class="numeric">Records</th>
          </tr>
        </thead>
        <tbody>
          <tr r-for="partition in status.engine.partitions">
            <td><code>{{ partition.directory }}</code></td>
            <td class="numeric">{{ partition.records === null ? '—' : number(partition.records) }}</td>
          </tr>
        </tbody>
      </table>
    </div>
    <p r-else>No partitions are open.</p>
  </Panel>
</div>`

const page = defineComponent(template, {
  context: () => {
    const status = sref<AdminStatus | null>(null)
    const error = sref('')
    const loading = sref(false)
    let timer = 0
    const refresh = async () => {
      loading(true)
      try {
        const next = await dashboardFetch<AdminStatus>('/admin/status')
        status(next)
        error('')
        // Follow background work closely, otherwise check occasionally.
        const busy =
          !!next.engine.indexMaintenance ||
          next.engine.state === 'Reopening' ||
          next.engine.state === 'Opening'
        window.clearTimeout(timer)
        timer = window.setTimeout(refresh, busy ? 2000 : 15000)
      } catch (failure) {
        error((failure as Error).message)
      } finally {
        loading(false)
      }
    }
    void loadMe()
      .then(refresh)
      .catch((failure: Error) => error(failure.message))
    window.addEventListener('pagehide', () => window.clearTimeout(timer), {
      once: true,
    })
    return {
      status,
      error,
      loading,
      refresh,
      formatDate,
      number: (value: number) => value.toLocaleString(),
      stateLabel: computed(
        () => stateLabels[status()?.engine.state ?? ''] ?? '—',
      ),
    }
  },
})

mountPage({ selector: '#admin-app', component: page })
