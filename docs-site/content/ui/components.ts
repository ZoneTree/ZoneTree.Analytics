import { defineComponent, html } from 'regor'
import { defineAnalyticsHero } from './hero'

export interface DocCard {
  number: string
  title: string
  summary: string
  href: string
  icon: string
}

export interface StackTechnology {
  name: string
  role: string
  summary: string
  website: string
  repository: string
  icon: string
}

export function defineDocsComponents() {
  return {
    AnalyticsHero: defineAnalyticsHero(),
    StackTechnology: defineComponent<StackTechnology>(
      html`<article class="docs-project">
  <div class="docs-project-heading">
    <Icon :name="icon"/><span>{{ role }}</span>
  </div>
  <h3><a :href="website">{{ name }}</a></h3>
  <p>{{ summary }}</p>
  <div class="docs-project-actions">
    <a :href="website">Documentation <Icon name="tabler:arrow-up-right"/></a>
    <a :href="repository" :aria-label="name + ' source on GitHub'"
      >Source <Icon name="tabler:brand-github"/></a>
  </div>
</article>`,
      { props: ['name', 'role', 'summary', 'website', 'repository', 'icon'] },
    ),
    DocCard: defineComponent<DocCard>(
      html`<a class="docs-card" :href="href">
  <div class="docs-card-meta">
    <Badge tone="accent" variant="surface">{{ number }}</Badge><Icon :name="icon"/>
  </div>
  <h3>{{ title }}</h3>
  <p>{{ summary }}</p>
  <span class="docs-card-link"
    >Explore <Icon name="tabler:arrow-up-right"/></span>
</a>`,
      { props: ['number', 'title', 'summary', 'href', 'icon'] },
    ),
  }
}
