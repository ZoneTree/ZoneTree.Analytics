import {
  type ThemePalette,
  type ThemeSkin,
  themeSkins,
} from '@purestack/ts-style'
import { merge } from '@purestack/ts-util'
import { dashboardColors } from './colors'

function palette(dark: boolean): ThemePalette {
  const standard = themeSkins.standard.create()
  const original = dark ? standard.dark : standard.light
  const colors = dashboardColors[dark ? 'dark' : 'light']
  const { ink, muted, surface, canvas, border, accent: signal } = colors
  const state = (bg: string, text: string, edge = border) => ({
    background: bg,
    bgcolor: bg,
    border: edge,
    text,
  })
  const rest = state(surface, ink)
  const states = (bg: string, text: string, edge = border) => ({
    rest: state(bg, text, edge),
    hover: state(bg, text, edge),
    active: state(bg, text, edge),
    disabled: state(bg, text, edge),
    focusRing: signal,
  })
  const tone = (text: string, bg: string, edge: string) => ({
    tone: text,
    canvas: bg,
    canvascolor: bg,
    overlay: bg,
    text: { default: text, subtle: text },
    border: { default: edge, subtle: edge, focus: signal },
    root: {
      text: { default: text, subtle: text },
      border: { default: edge, subtle: edge, focus: signal },
    },
    surface: states(bg, text, edge),
    surfaceAlt: states(bg, text, edge),
    button: states(text, dark ? colors.canvas : '#FFFFFF', edge),
    icon: { color: text, background: bg, gradient: bg, border: edge },
  })
  const neutral = merge(original.semanticTone.neutral, {
    canvas,
    canvascolor: canvas,
    tone: ink,
    overlay: colors.surfaceAlt,
    text: { default: ink, subtle: muted },
    border: { default: border, subtle: border, focus: signal },
    root: {
      text: { default: ink, subtle: muted },
      border: { default: border, subtle: border, focus: signal },
    },
    surface: {
      ...states(surface, ink),
      hover: state(colors.hover, ink),
      active: rest,
    },
    surfaceAlt: states(colors.surfaceAlt, ink),
    button: { ...states(surface, ink), hover: state(colors.hover, ink) },
    icon: { color: muted, background: surface, gradient: surface, border },
  })
  return merge(original, {
    accent: signal,
    font: {
      family: {
        base: "'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif",
      },
    },
    semanticTone: {
      neutral,
      accent: {
        ...tone(signal, colors.accentSoft, border),
        button: {
          ...states(signal, colors.accentInk),
          hover: state(colors.accentHover, colors.accentInk),
        },
      },
      success: tone(colors.success, colors.successSoft, colors.successBorder),
      warning: tone(colors.warning, colors.warningSoft, colors.warningBorder),
      danger: tone(colors.danger, colors.dangerSoft, colors.dangerBorder),
      info: tone(colors.info, colors.infoSoft, colors.infoBorder),
    },
    effect: { panelShadow: colors.shadow, softShadow: colors.shadow },
    radii: { sm: '6px', md: '10px', lg: '14px' },
  })
}
export const dashboardSkin: ThemeSkin = {
  create: () => ({ light: palette(false), dark: palette(true) }),
}
