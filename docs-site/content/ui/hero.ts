import type { Style } from '@purestack/ts-css'
import type { ThemeMode, ThemePalette } from '@purestack/ts-style'
import { defineComponent, html } from 'regor'
import { analyticsColors } from './skin'

export interface AnalyticsHero {
  benchmarkHref: string
}

export function defineAnalyticsHero() {
  return defineComponent<AnalyticsHero>(
    html`<section class="docs-hero" aria-labelledby="analytics-hero-title">
      <div class="docs-hero-layout">
        <div class="hero-introduction">
          <Badge tone="accent" variant="surface">Analytics Engine for .NET</Badge>
          <h1 id="analytics-hero-title">Millions of events.<br/><span>One clear picture.</span></h1>
          <p class="docs-hero-copy">Understand your traffic, your product, and the actions that matter. Powerful analytics inside your .NET application, with your data under your control.</p>
          <Flex class="docs-hero-actions">
            <BtnLink href="/docs/getting-started/first-events/" tone="accent" variant="solid" icon="tabler:arrow-right" iconPosition="end">Start building</BtnLink>
            <BtnLink href="/docs/getting-started/dashboard/" tone="neutral" variant="outline">Explore the dashboard</BtnLink>
          </Flex>
          <div class="docs-facts"><span>.NET 8 · 9 · 10</span><span>Open source · MIT</span><span>Your infrastructure</span></div>
        </div>
        <div class="hero-observatory" role="group" aria-label="Website, product, and service activity becomes insight through ZoneTree Analytics">
          <div class="hero-visual-heading"><span>Activity, made clear.</span><span class="hero-visual-index">01 / INSIGHT</span></div>
          <div class="hero-signal-field">
            <svg class="hero-signal-art" viewBox="0 0 640 420" fill="none" aria-hidden="true" focusable="false">
              <defs>
                <pattern id="analytics-hero-grid" width="24" height="24" patternUnits="userSpaceOnUse"><circle cx="1" cy="1" r="1" fill="currentColor"/></pattern>
                <radialGradient id="analytics-hero-halo"><stop stop-color="currentColor" stop-opacity=".18"/><stop offset="1" stop-color="currentColor" stop-opacity="0"/></radialGradient>
                <linearGradient id="analytics-hero-face" x1="230" y1="186" x2="394" y2="278" gradientUnits="userSpaceOnUse"><stop stop-color="currentColor" stop-opacity=".36"/><stop offset="1" stop-color="currentColor" stop-opacity=".08"/></linearGradient>
                <linearGradient id="analytics-hero-chart" x1="520" y1="120" x2="520" y2="280" gradientUnits="userSpaceOnUse"><stop stop-color="currentColor" stop-opacity=".24"/><stop offset="1" stop-color="currentColor" stop-opacity="0"/></linearGradient>
              </defs>
              <rect class="hero-grid" x="8" y="20" width="624" height="376" fill="url(#analytics-hero-grid)"/>
              <ellipse cx="314" cy="248" rx="218" ry="180" fill="url(#analytics-hero-halo)"/>
              <g class="hero-orbits" stroke="currentColor">
                <ellipse cx="312" cy="264" rx="145" ry="82"/>
                <ellipse cx="312" cy="264" rx="185" ry="108" stroke-dasharray="2 9"/>
                <path d="M312 132V164M312 362V386M108 264H132M494 264H518"/>
              </g>
              <g class="hero-streams" stroke="currentColor" stroke-width="1.5">
                <path d="M152 120C214 120 202 194 264 212"/>
                <path d="M152 200C202 200 202 224 238 234"/>
                <path d="M152 280C210 280 204 268 238 256"/>
                <path d="M210 68C270 68 280 138 304 192" stroke-dasharray="3 7"/>
                <path d="M462 322C426 330 404 284 378 274" stroke-dasharray="3 7"/>
              </g>
              <g class="hero-event-points" fill="currentColor"><circle cx="210" cy="68" r="4"/><circle cx="462" cy="322" r="4"/><circle cx="180" cy="330" r="2"/><circle cx="426" cy="92" r="2"/><circle cx="544" cy="72" r="2"/></g>
              <path d="M440 220L464 204L484 212L508 177L530 187L554 146L586 155L610 126V280H440Z" fill="url(#analytics-hero-chart)"/>
              <path class="hero-answer-trace" d="M394 232C420 232 420 220 440 220L464 204L484 212L508 177L530 187L554 146L586 155L610 126" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
              <circle cx="610" cy="126" r="5" fill="currentColor"/>
              <circle cx="610" cy="126" r="12" stroke="currentColor" opacity=".25"/>
              <g class="hero-core" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round">
                <path class="hero-core-base" d="M312 234L394 280L312 326L230 280Z"/>
                <path class="hero-core-middle" d="M312 210L394 256L312 302L230 256Z"/>
                <path class="hero-core-top" d="M312 186L394 232L312 278L230 232Z" fill="url(#analytics-hero-face)"/>
                <path d="M230 232V280M394 232V280M312 278V326" opacity=".6"/>
                <path d="M312 209L352 232L312 255L272 232Z" opacity=".6"/>
                <circle cx="312" cy="232" r="5" fill="currentColor" stroke="none"/>
              </g>
              <g class="hero-particles" fill="currentColor">
                <circle r="3" opacity="0"><animateMotion path="M152 120C214 120 202 194 264 212" dur="3s" begin=".2s" fill="freeze"/><animate attributeName="opacity" values="0;1;1;0" keyTimes="0;.08;.9;1" dur="3s" begin=".2s" fill="freeze"/></circle>
                <circle r="3" opacity="0"><animateMotion path="M152 200C202 200 202 224 238 234" dur="3s" begin=".6s" fill="freeze"/><animate attributeName="opacity" values="0;1;1;0" keyTimes="0;.08;.9;1" dur="3s" begin=".6s" fill="freeze"/></circle>
                <circle r="3" opacity="0"><animateMotion path="M152 280C210 280 204 268 238 256" dur="3s" begin="1s" fill="freeze"/><animate attributeName="opacity" values="0;1;1;0" keyTimes="0;.08;.9;1" dur="3s" begin="1s" fill="freeze"/></circle>
              </g>
            </svg>
            <div class="hero-source hero-source-web"><Icon name="tabler:world"/><span>Website traffic</span></div>
            <div class="hero-source hero-source-product"><Icon name="tabler:target"/><span>Product activity</span></div>
            <div class="hero-source hero-source-service"><Icon name="tabler:activity"/><span>Service telemetry</span></div>
            <div class="hero-engine-label"><strong>ZoneTree</strong><span>Analytics engine</span></div>
            <div class="hero-answer-label"><strong>Clear answers</strong><span>Trends. Behavior. Outcomes.</span></div>
          </div>
          <div class="hero-proof">
            <a :href="benchmarkHref"><strong>5M<span> events</span></strong><small>Measured dataset</small></a>
            <a :href="benchmarkHref"><strong>84<span> ms</span></strong><small>Six annual reports · median</small></a>
          </div>
          <a class="hero-benchmark-link" :href="benchmarkHref">Explore the benchmark <Icon name="tabler:arrow-up-right"/></a>
        </div>
      </div>
    </section>`,
    { props: ['benchmarkHref'] },
  )
}

export function registerHeroStyles(root: Style, palette: ThemePalette, theme: ThemeMode) {
  const neutral = palette.semanticTone.neutral
  const c = theme === 'dark' ? analyticsColors.dark : analyticsColors.light
  root.select('.docs-hero').css({ paddingBlock: '0 48px', borderBottom: `1px solid ${neutral.border.subtle}` })
  root.select('.doc-main:has(.docs-hero)').css({ width: 'min(1160px, calc(100% - 80px))', marginInline: 'auto' })
  root.select('.docs-hero-layout').css({ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '38px', alignItems: 'center' })
  root.select('.docs-hero-layout > div').css({ minWidth: '0' })
  root.select('.docs-hero h1').css({ fontSize: 'clamp(42px, 4.45vw, 64px)', lineHeight: '1.08', letterSpacing: '-2.8px', marginBlock: '26px', fontWeight: '700', textWrap: 'balance' })
  root.select('.docs-hero h1 span').css({ color: c.accentInk })
  root.select('.docs-hero-copy').css({ maxWidth: '46ch', color: neutral.text.subtle, fontSize: '19px', lineHeight: '1.7' })
  root.select('.docs-hero-actions').css({ gap: '12px', flexWrap: 'wrap', marginTop: '28px' })
  root.select('.docs-facts').css({ display: 'flex', flexWrap: 'wrap', gap: '18px', marginTop: '30px', color: neutral.text.subtle, fontSize: '12px' })
  root.select('.hero-observatory').css({ position: 'relative', padding: '20px 0 0' })
  root.select('.hero-visual-heading').css({ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '12px', paddingInline: '24px', fontSize: '12px', fontWeight: '600', letterSpacing: '.025em' })
  root.select('.hero-visual-index').css({ fontSize: '10px', color: neutral.text.subtle, letterSpacing: '.1em' })
  root.select('.hero-signal-field').css({ position: 'relative', aspectRatio: '640 / 420', marginBlock: '4px 16px' })
  root.select('.hero-signal-art').css({ display: 'block', width: '100%', height: '100%', overflow: 'visible', color: c.signal })
  root.select('.hero-grid').css({ color: neutral.text.subtle, opacity: '.15' })
  root.select('.hero-orbits').css({ color: neutral.border.default, opacity: '.5' })
  root.select('.hero-streams').css({ opacity: '.55' })
  root.select('.hero-event-points').css({ opacity: '.6' })
  root.select('.hero-core').css({ filter: `drop-shadow(0 12px 22px ${c.signal}24)`, transition: 'transform 250ms ease', transformOrigin: 'center', transformBox: 'fill-box' })
  root.select('.hero-core-base').css({ fill: c.accentSurface })
  root.select('.hero-core-middle').css({ fill: `color-mix(in srgb, ${c.signal} 13%, ${neutral.canvas})` })
  root.select('.hero-core-top').css({ strokeWidth: '2' })
  root.select('.hero-observatory:hover .hero-core').css({ transform: 'translateY(-4px)' })
  root.select('.hero-source').css({ position: 'absolute', left: '3%', display: 'flex', alignItems: 'center', gap: '9px', padding: '8px 12px', fontSize: '12px', lineHeight: '1.4', border: `1px solid ${neutral.border.subtle}`, borderRadius: '100px', background: `color-mix(in srgb, ${neutral.surface.rest.bgcolor} 92%, transparent)`, boxShadow: `0 4px 16px ${theme === 'dark' ? '#00000018' : '#00000004'}`, whiteSpace: 'nowrap', transform: 'translateY(-50%)' })
  root.select('.hero-source .icon').css({ color: c.accentInk, width: '15px', height: '15px' })
  root.select('.hero-source-web').css({ top: '28.57%' })
  root.select('.hero-source-product').css({ top: '47.62%' })
  root.select('.hero-source-service').css({ top: '66.67%' })
  root.select('.hero-engine-label, .hero-answer-label').css({ position: 'absolute', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '2px', fontSize: '12px', lineHeight: '1.6', textAlign: 'center' })
  root.select('.hero-engine-label').css({ top: '82%', left: '48.75%', transform: 'translateX(-50%)' })
  root.select('.hero-engine-label strong').css({ letterSpacing: '-.2px', fontSize: '15px', fontWeight: '650' })
  root.select('.hero-engine-label span, .hero-answer-label span').css({ color: neutral.text.subtle, fontSize: '10px' })
  root.select('.hero-answer-label').css({ top: '70%', left: '70%', width: '29%' })
  root.select('.hero-proof').css({ display: 'grid', gridTemplateColumns: '1fr 1fr', borderTop: `1px solid ${neutral.border.subtle}`, paddingBlock: '22px 10px', marginInline: '24px', gap: '24px' })
  root.select('.hero-proof a').css({ display: 'flex', flexDirection: 'column', gap: '7px', color: neutral.text.default, textDecoration: 'none' })
  root.select('.hero-proof a + a').css({ paddingLeft: '24px', borderLeft: `1px solid ${neutral.border.subtle}` })
  root.select('.hero-proof strong').css({ fontSize: '36px', fontWeight: '600', letterSpacing: '-1.5px', lineHeight: '1.1', fontVariantNumeric: 'tabular-nums' })
  root.select('.hero-proof strong span').css({ color: neutral.text.subtle, fontSize: '18px', fontWeight: '400', letterSpacing: '-.3px' })
  root.select('.hero-proof small').css({ color: neutral.text.subtle, fontSize: '11px', lineHeight: '1.5' })
  root.select('.hero-benchmark-link').css({ display: 'inline-flex', alignItems: 'center', gap: '5px', margin: '12px 24px 0', fontSize: '11px', fontWeight: '600', color: c.accentInk, textDecoration: 'none' })
  root.select('.hero-proof a:hover strong, .hero-benchmark-link:hover').css({ color: c.accentInk })
  root.select('.hero-observatory a:focus-visible').css({ outline: `2px solid ${c.accentInk}`, outlineOffset: '5px', borderRadius: '2px' })
  root.media('max-width: 1050px').select('.docs-hero-layout').css({ gridTemplateColumns: '1fr', gap: '32px' })
  root.media('max-width: 1050px').select('.hero-introduction').css({ maxWidth: '740px' })
  root.media('max-width: 1050px').select('.hero-observatory').css({ width: 'min(100%, 640px)', justifySelf: 'center' })
  root.media('max-width: 760px').select('.docs-hero').css({ paddingBottom: '32px' })
  root.media('max-width: 760px').select('.doc-main:has(.docs-hero)').css({ width: 'calc(100% - 32px)' })
  root.media('max-width: 760px').select('.docs-hero h1').css({ fontSize: 'clamp(38px, 7.5vw, 56px)', letterSpacing: '-1.8px' })
  root.media('max-width: 760px').select('.docs-hero-copy').css({ fontSize: '17px' })
  root.media('max-width: 460px').select('.hero-visual-heading').css({ paddingInline: '4px' })
  root.media('max-width: 460px').select('.hero-source').css({ padding: '6px 8px', gap: '5px', fontSize: '10px', left: '0' })
  root.media('max-width: 460px').select('.hero-source .icon').css({ width: '12px', height: '12px' })
  root.media('max-width: 460px').select('.hero-engine-label strong').css({ fontSize: '12px' })
  root.media('max-width: 460px').select('.hero-answer-label').css({ fontSize: '10px' })
  root.media('max-width: 460px').select('.hero-engine-label span, .hero-answer-label span').css({ fontSize: '9px' })
  root.media('max-width: 460px').select('.hero-proof').css({ marginInline: '4px', gap: '16px' })
  root.media('max-width: 460px').select('.hero-proof a + a').css({ paddingLeft: '16px' })
  root.media('max-width: 460px').select('.hero-proof strong').css({ fontSize: '31px' })
  root.media('max-width: 460px').select('.hero-benchmark-link').css({ marginLeft: '4px' })
  root.media('prefers-reduced-motion: reduce').select('.hero-particles').css({ display: 'none' })
  root.media('prefers-reduced-motion: reduce').select('.hero-core').css({ transition: 'none', transform: 'none !important' })
}
