import type { Style } from '@purestack/ts-css'
import type { ThemeMode, ThemePalette } from '@purestack/ts-style'
import { type DashboardThemeColors, dashboardColors } from '../colors'

export interface DashboardStyleContext {
  root: Style
  theme: ThemeMode
  palette: ThemePalette
  neutral: ThemePalette['semanticTone']['neutral']
  ink: string
  muted: string
  edge: string
  surface: string
  accent: string
  dark: boolean
  colors: DashboardThemeColors
}

export function createDashboardStyleContext(
  root: Style,
  theme: ThemeMode,
  palette: ThemePalette,
): DashboardStyleContext {
  const neutral = palette.semanticTone.neutral
  return {
    root,
    theme,
    palette,
    neutral,
    ink: neutral.text.default,
    muted: neutral.text.subtle,
    edge: neutral.border.subtle,
    surface: neutral.surface.rest.bgcolor,
    accent: palette.accent,
    dark: theme === 'dark',
    colors: dashboardColors[theme],
  }
}
