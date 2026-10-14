export interface DashboardThemeColors {
  canvas: string
  surface: string
  surfaceAlt: string
  hover: string
  ink: string
  muted: string
  border: string
  accent: string
  accentHover: string
  accentInk: string
  accentSoft: string
  accentSoftHover: string
  success: string
  successSoft: string
  successBorder: string
  warning: string
  warningSoft: string
  warningBorder: string
  danger: string
  dangerSoft: string
  dangerBorder: string
  info: string
  infoSoft: string
  infoBorder: string
  chartCurrent: string
  chartPrevious: string
  chartGold: string
  chartRose: string
  shadow: string
  overlay: string
}

export const dashboardColors = {
  light: {
    canvas: '#F7F7F9',
    surface: '#FFFFFF',
    surfaceAlt: '#FAFAFC',
    hover: '#F1F1F5',
    ink: '#202024',
    muted: '#6C6C78',
    border: '#E6E6EC',
    accent: '#9D35BB',
    accentHover: '#83269F',
    accentInk: '#FFFFFF',
    accentSoft: '#FAEFFD',
    accentSoftHover: '#F3DFFA',
    success: '#167B58',
    successSoft: '#EAF8F1',
    successBorder: '#BDE7D3',
    warning: '#8A6115',
    warningSoft: '#FFF8E6',
    warningBorder: '#EDDFB6',
    danger: '#BA3A60',
    dangerSoft: '#FFF0F5',
    dangerBorder: '#F0C9D6',
    info: '#74428A',
    infoSoft: '#F6EFFA',
    infoBorder: '#E4D2EF',
    chartCurrent: '#B344D0',
    chartPrevious: '#219B75',
    chartGold: '#D9AA43',
    chartRose: '#DF7297',
    shadow: '0 4px 24px #18181B05, 0 1px 3px #18181B03',
    overlay: '#15151888',
  },
  dark: {
    canvas: '#111113',
    surface: '#1B1B1F',
    surfaceAlt: '#212126',
    hover: '#29292F',
    ink: '#F4F4F6',
    muted: '#A5A5B2',
    border: '#323238',
    accent: '#DD9CF0',
    accentHover: '#E9B9F7',
    accentInk: '#2D1237',
    accentSoft: '#33223C',
    accentSoftHover: '#442C50',
    success: '#68DAB0',
    successSoft: '#1B332B',
    successBorder: '#2B5948',
    warning: '#EDCB85',
    warningSoft: '#332D21',
    warningBorder: '#55482E',
    danger: '#F29AB8',
    dangerSoft: '#38232E',
    dangerBorder: '#5C3547',
    info: '#D1AFE4',
    infoSoft: '#2F2538',
    infoBorder: '#4D3B5E',
    chartCurrent: '#DD9CF0',
    chartPrevious: '#68DAB0',
    chartGold: '#EDCB85',
    chartRose: '#F29AB8',
    shadow: '0 8px 28px #00000020, 0 1px 3px #00000012',
    overlay: '#09090BB3',
  },
} satisfies Record<'light' | 'dark', DashboardThemeColors>

export const sidebarColors = {
  canvas: '#161618',
  ink: '#F8F8FA',
  muted: '#A2A2AE',
  border: '#343439',
  surface: '#242429',
  hover: '#29292F',
  link: '#C6C6D1',
  accent: '#DD9CF0',
  accentInk: '#2D1237',
  selection: '#35243E',
  mint: '#68DAB0',
}
