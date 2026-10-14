import {
  type ThemePalette,
  type ThemeSkin,
  themeSkins,
} from '@purestack/ts-style'
import { merge } from '@purestack/ts-util'

type Tone = ThemePalette['semanticTone']['neutral']

// Orange accents on neutral paper and charcoal, with quiet graphite supporting tones.
export const analyticsColors = {
  light: {
    signal: '#fb7700',
    signalHover: '#ea6900',
    signalActive: '#db5c00',
    onSignal: '#201207',
    canvas: '#f8f8f8',
    surface: '#ffffff',
    surfaceAlt: '#efefef',
    hover: '#e7e7e7',
    active: '#dddddd',
    ink: '#262626',
    muted: '#666666',
    border: '#d1d1d1',
    subtle: '#e2e2e2',
    accentInk: '#b64a00',
    accentSurface: '#ffebdd',
    accentHover: '#ffd8b8',
    accentActive: '#ffc795',
    accentBorder: '#dc994f',
    companionInk: '#545454',
    companionFill: '#5b5b5b',
    companionHover: '#4b4b4b',
    companionActive: '#3d3d3d',
    companionSurface: '#f0f0f0',
    companionBorder: '#cfcfcf',
    onCompanion: '#ffffff',
  },
  dark: {
    signal: '#fa8b17',
    signalHover: '#ffa037',
    signalActive: '#e97908',
    onSignal: '#241305',
    canvas: '#151515',
    surface: '#212121',
    surfaceAlt: '#1b1b1b',
    hover: '#2d2d2d',
    active: '#393939',
    ink: '#f2f2f2',
    muted: '#b9b9b9',
    border: '#505050',
    subtle: '#353535',
    accentInk: '#ff941f',
    accentSurface: '#352415',
    accentHover: '#493017',
    accentActive: '#603d1b',
    accentBorder: '#a86b2b',
    companionInk: '#dadada',
    companionFill: '#c7c7c7',
    companionHover: '#dddddd',
    companionActive: '#b0b0b0',
    companionSurface: '#303030',
    companionBorder: '#666666',
    onCompanion: '#252525',
  },
} as const

function state(background: string, border: string, text: string) {
  return { background, bgcolor: background, border, text }
}

function createNeutral(dark: boolean): Tone {
  const c = dark ? analyticsColors.dark : analyticsColors.light
  const text = { default: c.ink, subtle: c.muted }
  const border = { default: c.border, subtle: c.subtle, focus: c.accentInk }
  const disabled = state(c.surfaceAlt, c.subtle, c.muted)
  const surface = {
    rest: state(c.surface, c.subtle, c.ink),
    hover: state(c.hover, c.border, c.ink),
    active: state(c.active, c.border, c.ink),
    disabled,
    focusRing: c.accentInk,
  }
  return {
    tone: c.ink,
    canvas: c.canvas,
    canvascolor: c.canvas,
    spotlight: { field: c.canvas, light: `${c.signal}12` },
    overlay: dark ? '#0a0a0acc' : '#26262644',
    text,
    border,
    root: { text, border },
    surface,
    surfaceAlt: { ...surface, rest: state(c.surfaceAlt, c.subtle, c.ink) },
    button: {
      ...surface,
      rest: state(c.surfaceAlt, c.border, c.ink),
    },
  }
}

function createAccent(dark: boolean, neutral: Tone): Tone {
  const c = dark ? analyticsColors.dark : analyticsColors.light
  const text = { default: c.accentInk, subtle: c.muted }
  const border = {
    default: c.accentBorder,
    subtle: c.subtle,
    focus: c.accentInk,
  }
  const surface = {
    rest: state(c.accentSurface, c.accentBorder, c.ink),
    hover: state(c.accentHover, c.accentInk, c.ink),
    active: state(c.accentActive, c.accentInk, c.ink),
    disabled: neutral.surface.disabled,
    focusRing: c.accentInk,
  }
  return {
    tone: c.signal,
    canvas: c.accentSurface,
    canvascolor: c.accentSurface,
    spotlight: { field: c.accentSurface, light: `${c.signal}20` },
    overlay: neutral.overlay,
    text,
    border,
    root: { text, border },
    surface,
    surfaceAlt: surface,
    button: {
      rest: state(c.signal, c.signal, c.onSignal),
      hover: state(
        c.signalHover,
        c.signalHover,
        c.onSignal,
      ),
      active: state(
        c.signalActive,
        c.signalActive,
        c.onSignal,
      ),
      disabled: neutral.button.disabled,
      focusRing: c.accentInk,
    },
  }
}

function createCompanion(dark: boolean, neutral: Tone): Tone {
  const c = dark ? analyticsColors.dark : analyticsColors.light
  const ink = c.companionInk
  const fill = c.companionFill
  const field = c.companionSurface
  const edge = c.companionBorder
  const text = { default: ink, subtle: neutral.text.subtle }
  const border = { default: edge, subtle: edge, focus: ink }
  const surface = {
    rest: state(field, edge, ink),
    hover: state(field, ink, ink),
    active: state(field, ink, ink),
    disabled: neutral.surface.disabled,
    focusRing: ink,
  }
  return {
    tone: fill,
    canvas: field,
    canvascolor: field,
    spotlight: { field, light: `${fill}20` },
    overlay: neutral.overlay,
    text,
    border,
    root: { text, border },
    surface,
    surfaceAlt: surface,
    button: {
      rest: state(fill, fill, c.onCompanion),
      hover: state(
        c.companionHover,
        c.companionHover,
        c.onCompanion,
      ),
      active: state(c.companionActive, c.companionActive, c.onCompanion),
      disabled: neutral.button.disabled,
      focusRing: ink,
    },
  }
}

function createPalette(dark: boolean): ThemePalette {
  const c = dark ? analyticsColors.dark : analyticsColors.light
  const standard = themeSkins.standard.create()
  const neutral = createNeutral(dark)
  const accent = createAccent(dark, neutral)
  const companion = createCompanion(dark, neutral)
  const invisible = state('transparent', 'transparent', neutral.text.default)
  const ghost = {
    ...neutral,
    canvas: 'transparent',
    canvascolor: 'transparent',
    surface: { ...neutral.surface, rest: invisible },
    button: { ...neutral.button, rest: invisible },
  }
  return merge(dark ? standard.dark : standard.light, {
    accent: c.signal,
    font: {
      family: {
        base: "'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif",
      },
    },
    radii: { sm: '6px', md: '10px', lg: '16px' },
    semanticTone: {
      neutral,
      accent,
      ghost,
      secondary: companion,
      feature: companion,
      info: companion,
      custom: accent,
    },
    effect: {
      panelShadow: 'none',
      softShadow: dark ? '0 8px 28px #00000022' : '0 8px 28px #00000008',
      floatingShadow: dark ? '0 28px 80px #00000055' : '0 28px 80px #00000014',
      glowPrimary: `0 0 48px ${c.signal}18`,
      glowSecondary: `0 0 32px ${companion.tone}12`,
      accentShadow: `0 8px 24px ${c.signal}18`,
      focusGlow: `0 0 0 3px ${c.accentInk}44`,
    },
  })
}

export const analyticsSkin: ThemeSkin = {
  create: () => ({ light: createPalette(false), dark: createPalette(true) }),
}

