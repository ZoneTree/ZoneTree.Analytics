import { defineComponent, html, type SRef } from 'regor'
import type { DashboardModel } from '../lib/dashboard-model'

export interface OverviewBreakdowns {
  dashboard: DashboardModel | SRef<DashboardModel>
}

const template = html`
  <div class="overview-grid">
    <ReportBreakdown
      title="Top sources"
      subtitle="Where your visitors come from"
      column="SOURCE"
      :href="dashboard.assetPath('acquisition/')"
      :rows="dashboard.sources"
      dimension="source"
      :onFilter="dashboard.addFilter"
      :countryName="dashboard.countryName"
      :number="dashboard.number"
      :percent="dashboard.percent" />
    <OverviewTopPages :dashboard="dashboard" />
    <ReportBreakdown
      title="Countries"
      subtitle="A world of visitors"
      column="COUNTRY"
      :href="dashboard.assetPath('audience/')"
      :rows="dashboard.countries"
      dimension="country"
      :onFilter="dashboard.addFilter"
      :countryName="dashboard.countryName"
      :number="dashboard.number"
      :percent="dashboard.percent" />
    <OverviewDevices :dashboard="dashboard" />
  </div>
`

export function defineOverviewBreakdowns() {
  return defineComponent<OverviewBreakdowns>(template, { props: ['dashboard'] })
}
