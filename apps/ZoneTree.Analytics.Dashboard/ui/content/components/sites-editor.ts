import {
  type ComputedRef,
  computed,
  defineComponent,
  html,
  type RefOrValue,
  type SRef,
  sref,
} from 'regor'
import type { SiteSettings } from '../lib/admin-contracts'
import { splitList } from '../lib/settings-model'

export interface SiteDraft {
  name: SRef<string>
  id: SRef<string>
  domains: SRef<string>
  // Existing sites keep their ID: their data is stored under it.
  locked: boolean
  idTouched: boolean
}

export interface SitesEditor {
  sites: SRef<SiteDraft[]>
  primarySite: SRef<string>
  unmatched: SRef<string>
  primaryOptions: ComputedRef<{ value: string; label: string }[]>
  unmatchedOptions: { value: string; label: string }[]
  add: () => void
  remove: (site: SiteDraft) => void
  nameChanged: (site: SiteDraft) => void
  idChanged: (site: SiteDraft) => void
  collect: () => {
    items: SiteSettings[]
    primarySite: string
    unmatchedTraffic: string
  }
  load: (
    items: SiteSettings[],
    primarySite: string,
    unmatchedTraffic: string,
  ) => void
}

export function siteSlug(name: string): string {
  return (
    name
      .toLowerCase()
      .normalize('NFKD')
      .replace(/[̀-ͯ]/g, '')
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 64) || 'site'
  )
}

export function createSitesEditor(
  items: SiteSettings[],
  primarySite: string,
  unmatchedTraffic: string,
  lockExisting: boolean,
): SitesEditor {
  const draft = (site: SiteSettings, locked: boolean): SiteDraft => ({
    name: sref(site.name),
    id: sref(site.id),
    domains: sref(site.domains.join(', ')),
    locked,
    idTouched: locked,
  })
  const sites = sref(items.map((site) => draft(site, lockExisting)))
  const primary = sref(primarySite)
  const unmatched = sref(unmatchedTraffic)
  const ensurePrimary = () => {
    if (!sites().some((site) => site.id() === primary()))
      primary(sites()[0]?.id() ?? '')
  }
  const uniqueId = (base: string, except: SiteDraft) => {
    let id = base
    for (
      let n = 2;
      sites().some((site) => site !== except && site.id() === id);
      n++
    )
      id = `${base}-${n}`
    return id
  }
  return {
    sites,
    primarySite: primary,
    unmatched,
    primaryOptions: computed(() =>
      sites().map((site) => ({
        value: site.id(),
        label: site.name() || site.id(),
      })),
    ),
    unmatchedOptions: [
      { value: 'PrimarySite', label: 'Record them in the primary site' },
      { value: 'Ignore', label: 'Ignore them' },
    ],
    add: () => {
      const site = draft({ id: '', name: '', domains: [] }, false)
      site.id(uniqueId('site', site))
      sites([...sites(), site])
      ensurePrimary()
    },
    remove: (site) => {
      if (sites().length < 2) return
      sites(sites().filter((item) => item !== site))
      ensurePrimary()
    },
    nameChanged: (site) => {
      if (site.idTouched) return
      const renamesPrimary = primary() === site.id()
      site.id(uniqueId(siteSlug(site.name()), site))
      if (renamesPrimary) primary(site.id())
      ensurePrimary()
    },
    idChanged: (site) => {
      site.idTouched = true
      ensurePrimary()
    },
    load: (next, nextPrimary, nextUnmatched) => {
      sites(next.map((site) => draft(site, lockExisting)))
      primary(nextPrimary)
      unmatched(nextUnmatched)
      ensurePrimary()
    },
    collect: () => {
      const result = sites().map((site) => ({
        id: site.id().trim(),
        name: site.name().trim(),
        domains: splitList(site.domains()),
      }))
      if (!result.length) throw new Error('Add at least one site.')
      for (const site of result) {
        if (!site.name) throw new Error('Give every site a name.')
        if (!/^[a-z0-9][a-z0-9._-]{0,63}$/.test(site.id))
          throw new Error(
            `Site ID "${site.id}" must be lowercase letters, digits, dots, dashes or underscores.`,
          )
      }
      if (new Set(result.map((site) => site.id)).size !== result.length)
        throw new Error('Each site needs its own ID.')
      if (!result.some((site) => site.id === primary())) primary(result[0].id)
      return {
        items: result,
        primarySite: primary(),
        unmatchedTraffic: unmatched(),
      }
    },
  }
}

const template = html`<div class="sites-editor">
  <div class="site-row" r-for="site in editor.sites">
    <label>
      Site name
      <input
        type="text"
        r-model="site.name"
        @input="editor.nameChanged(site)"
        maxlength="100"
        placeholder="My website"
        required/>
    </label>
    <label>
      Site ID
      <input
        type="text"
        r-model="site.id"
        @input="editor.idChanged(site)"
        :readonly="site.locked"
        maxlength="64"
        required/>
    </label>
    <label class="site-domains">
      Domains
      <input
        type="text"
        r-model="site.domains"
        placeholder="example.com, *.example.com"/>
    </label>
    <Btn
      class="icon-button site-remove"
      variant="none"
      icon="tabler:trash"
      aria-label="Remove site"
      title="Remove site"
      :disabled="editor.sites.length < 2"
      @click="editor.remove(site)"/>
  </div>
  <p class="field-hint">
    Pages on these domains report to the site. *.example.com covers example.com
    and its subdomains. The site ID names the site in reports and the API; it
    cannot change once data is stored.
  </p>
  <Btn class="button" variant="outline" icon="tabler:plus" @click="editor.add"
    >Add site</Btn>
  <div class="form-grid sites-options">
    <label>
      Primary site
      <select r-model="editor.primarySite">
        <option r-for="option in editor.primaryOptions" :value="option.value">
          {{ option.label }}
        </option>
      </select>
      <span class="field-hint"
        >Opens first in reports and receives events that name no site.</span>
    </label>
    <label>
      Traffic from other domains
      <select r-model="editor.unmatched">
        <option r-for="option in editor.unmatchedOptions" :value="option.value">
          {{ option.label }}
        </option>
      </select>
      <span class="field-hint"
        >Events from pages whose host matches no site's domains.</span>
    </label>
  </div>
</div>`

export function defineSitesEditor() {
  return defineComponent<{ editor: RefOrValue<SitesEditor> }>(template, {
    props: ['editor'],
  })
}

export function trackingSnippet(origin: string): string {
  return `<script defer src="${origin}/_analytics/script.js"></script>`
}
