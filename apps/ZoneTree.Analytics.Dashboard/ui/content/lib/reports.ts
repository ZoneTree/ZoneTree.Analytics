import type {
  Bot,
  Breakdown,
  Goal,
  Page,
  QueryResult,
  Summary,
} from './contracts'
import { change, duration, number, percent } from './format'

export const reportInfo = {
  overview: {
    title: 'Overview',
    description: 'A little clarity on how your website is doing.',
  },
  pages: {
    title: 'Pages',
    description:
      'Discover the pages people visit, enter through, and leave from.',
  },
  sources: {
    title: 'Acquisition',
    description: 'Understand where your visitors are coming from.',
  },
  audience: {
    title: 'Audience',
    description: 'Get to know the people on the other side of the screen.',
  },
  goals: {
    title: 'Events & goals',
    description: 'See the actions that turn visits into something valuable.',
  },
  bots: {
    title: 'Bot traffic',
    description: 'See crawler activity alongside your human traffic.',
  },
  funnels: {
    title: 'Funnels',
    description: 'Follow the journey from first visit to conversion.',
  },
  explorer: {
    title: 'Query explorer',
    description: 'Ask your data a more specific question.',
  },
  settings: {
    title: 'Connection',
    description: 'Connect this dashboard to your analytics engine.',
  },
  'sample-data': {
    title: 'Sample data',
    description: 'Generate and manage traffic for performance testing.',
  },
} as const
export type ReportKind = keyof typeof reportInfo
export const metricDefinitions = [
  {
    key: 'visitors',
    label: 'Unique visitors',
    format: number,
    lowerBetter: false,
  },
  { key: 'pageViews', label: 'Page views', format: number, lowerBetter: false },
  { key: 'sessions', label: 'Sessions', format: number, lowerBetter: false },
  {
    key: 'bounceRate',
    label: 'Bounce rate',
    format: percent,
    lowerBetter: true,
  },
  {
    key: 'averageSessionDuration',
    label: 'Avg. visit duration',
    format: duration,
    lowerBetter: false,
  },
] as const
export type MetricKey = (typeof metricDefinitions)[number]['key']
export function metricCards(summary: Summary | null) {
  return metricDefinitions.map((metric) => {
    const value = summary?.[metric.key]
    const delta = change(value, summary?.previous?.[metric.key])
    return {
      key: metric.key,
      label: metric.label,
      value: metric.format(value),
      available: value != null,
      delta:
        delta == null
          ? 'No comparison available'
          : `${delta >= 0 ? '↗' : '↘'} ${percent(Math.abs(delta))}`,
      hasDelta: delta != null,
      negative: delta != null && (metric.lowerBetter ? delta > 0 : delta < 0),
    }
  })
}
export interface Cell {
  text: string
  dimension?: string
  value?: string
  numeric?: boolean
}
export interface TableModel {
  headers: string[]
  rows: { cells: Cell[] }[]
  exportRows: unknown[][]
}
export function countryName(value: string | null) {
  if (!value) return 'Unknown'
  try {
    return (
      new Intl.DisplayNames('en', { type: 'region' }).of(value.toUpperCase()) ||
      value
    )
  } catch {
    return value
  }
}
const drill = (
  dimension: string,
  value: string | null,
  text = value,
): Cell => ({
  text: text || (value === '' ? '(empty)' : 'Unknown'),
  ...(value != null ? { dimension, value } : {}),
})
const numeric = (text: string): Cell => ({ text, numeric: true })
export function pagesTable(rows: Page[]): TableModel {
  const headers = [
    'Page',
    'Visitors',
    'Views',
    'Entrances',
    'Exits',
    'Exit rate',
    'Bot hits',
  ]
  const compared = rows.some((row) => row.previous)
  if (compared) headers.push('View change')
  return {
    headers,
    rows: rows.map((row) => ({
      cells: [
        drill('page', row.page),
        ...[row.visitors, row.pageViews, row.entrances, row.exits].map(
          (value) => numeric(number(value)),
        ),
        numeric(percent(row.exitRate)),
        numeric(number(row.botHits)),
        ...(compared
          ? [numeric(percent(change(row.pageViews, row.previous?.pageViews)))]
          : []),
      ],
    })),
    exportRows: [
      headers,
      ...rows.map((row) => [
        row.page,
        row.visitors,
        row.pageViews,
        row.entrances,
        row.exits,
        row.exitRate,
        row.botHits,
        ...(compared ? [change(row.pageViews, row.previous?.pageViews)] : []),
      ]),
    ],
  }
}
export function breakdownTable(
  rows: Breakdown[],
  dimension: string,
): TableModel {
  const headers = [
    'Name',
    'Visitors',
    'Share',
    'Page views',
    'Sessions',
    'Bounce rate',
    'Avg. duration',
  ]
  const compared = rows.some((row) => row.previous)
  if (compared) headers.push('Visitor change')
  return {
    headers,
    rows: rows.map((row) => ({
      cells: [
        drill(
          dimension,
          row.value,
          dimension === 'country' ? countryName(row.value) : row.value,
        ),
        numeric(number(row.visitors)),
        numeric(percent(row.visitorShare)),
        numeric(number(row.pageViews)),
        numeric(number(row.sessions)),
        numeric(percent(row.bounceRate)),
        numeric(duration(row.averageSessionDuration)),
        ...(compared
          ? [numeric(percent(change(row.visitors, row.previous?.visitors)))]
          : []),
      ],
    })),
    exportRows: [
      headers,
      ...rows.map((row) => [
        row.value,
        row.visitors,
        row.visitorShare,
        row.pageViews,
        row.sessions,
        row.bounceRate,
        row.averageSessionDuration,
        ...(compared ? [change(row.visitors, row.previous?.visitors)] : []),
      ]),
    ],
  }
}
export function goalsTable(rows: Goal[]): TableModel {
  const headers = ['Event', 'Visitors', 'Events', 'Conversion rate']
  return {
    headers,
    rows: rows.map((row) => ({
      cells: [
        { text: row.name },
        numeric(number(row.visitors)),
        numeric(number(row.events)),
        numeric(percent(row.conversionRate)),
      ],
    })),
    exportRows: [
      headers,
      ...rows.map((row) => [
        row.name,
        row.visitors,
        row.events,
        row.conversionRate,
      ]),
    ],
  }
}
export function botsTable(rows: Bot[]): TableModel {
  const headers = ['Bot', 'Category', 'Requests', 'Share']
  const compared = rows.some((row) => row.previous)
  if (compared) headers.push('Request change')
  return {
    headers,
    rows: rows.map((row) => ({
      cells: [
        { text: row.name || 'Unknown' },
        { text: row.category || 'Unknown' },
        numeric(number(row.hits)),
        numeric(percent(row.share)),
        ...(compared
          ? [numeric(percent(change(row.hits, row.previous?.hits)))]
          : []),
      ],
    })),
    exportRows: [
      headers,
      ...rows.map((row) => [
        row.name,
        row.category,
        row.hits,
        row.share,
        ...(compared ? [change(row.hits, row.previous?.hits)] : []),
      ]),
    ],
  }
}
export function queryTable(result: QueryResult): TableModel {
  const bucket = result.rows.some((row) => row.bucket)
  const headers = [
    ...(bucket ? ['Time'] : []),
    ...result.groupBy,
    ...result.metrics,
  ]
  return {
    headers,
    rows: result.rows.map((row) => ({
      cells: [
        ...(bucket ? [{ text: row.bucket || '' }] : []),
        ...result.groupBy.map((key) => ({
          text: row.dimensions?.[key] ?? 'Unknown',
        })),
        ...result.metrics.map((key) => numeric(number(row.values[key]))),
      ],
    })),
    exportRows: [
      headers,
      ...result.rows.map((row) => [
        ...(bucket ? [row.bucket || ''] : []),
        ...result.groupBy.map((key) => row.dimensions?.[key]),
        ...result.metrics.map((key) => row.values[key]),
      ]),
    ],
  }
}
export const queryPresets: Record<string, Record<string, unknown>> = {
  traffic: {
    event: 'pageview',
    groupBy: ['source'],
    metrics: ['visitors', 'pageviews', 'bounce_rate'],
    orderBy: ['-visitors'],
    limit: 100,
  },
  revenue: {
    event: 'purchase',
    groupBy: ['plan'],
    metrics: ['events', 'sum:revenue'],
    orderBy: ['-sum:revenue'],
    limit: 100,
  },
  latency: {
    event: 'pageview',
    groupBy: ['page'],
    metrics: ['p95:load_time', 'avg:load_time'],
    orderBy: ['-p95:load_time'],
    limit: 100,
  },
  trend: {
    event: 'pageview',
    interval: 'day',
    metrics: ['visitors', 'moving_avg(visitors,7)'],
    limit: 1000,
  },
}
