import type {
  Bot,
  Breakdown,
  FunnelResult,
  Goal,
  Page,
  Plan,
  Point,
  QueryResult,
  Realtime,
  Report,
  Scope,
  Summary,
  SummaryReport,
} from './contracts'

// Deterministic synthetic sessions. Demo filters, dates, breakdowns and totals all
// derive from the same observations; nothing is fetched from an analytics server.
interface Session {
  day: string
  hour: number
  visitor: string
  id: string
  dimensions: Record<string, string>
  pages: string[]
  goals: string[]
  duration: number
}
const plan: Plan = {
  complete: true,
  warnings: [],
  steps: [],
  rollupSuggestions: [],
}
const sources = [
  'Direct',
  'Google',
  'Google',
  'GitHub',
  'Bing',
  'LinkedIn',
  'Newsletter',
  'ChatGPT',
]
const countries = ['US', 'US', 'DE', 'GB', 'FR', 'IN', 'CA', 'NL', 'AU']
const pages = [
  '/',
  '/',
  '/docs',
  '/docs/getting-started',
  '/pricing',
  '/blog',
  '/integrations',
  '/about',
]
const DAY = 86_400_000
const date = (value: Date) => value.toISOString().slice(0, 10)
const seeded = (seed: number) => () => {
  seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0
  return seed / 4294967296
}
export function demoDates(scope: Scope, now = new Date()): [Date, Date] {
  const start = new Date(
    Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()),
  )
  const end = new Date(+start + DAY)
  if (scope.range === 'custom')
    return [
      new Date(`${scope.from}T00:00:00Z`),
      new Date(+new Date(`${scope.to}T00:00:00Z`) + DAY),
    ]
  if (scope.range === 'yesterday') return [new Date(+start - DAY), start]
  if (scope.range === 'this-month') start.setUTCDate(1)
  else if (scope.range === 'last-month') {
    end.setUTCDate(1)
    start.setUTCDate(1)
    start.setUTCMonth(start.getUTCMonth() - 1)
  } else if (scope.range === 'this-year') {
    start.setUTCMonth(0, 1)
  } else if (scope.range !== 'today')
    start.setUTCDate(start.getUTCDate() - (scope.range === '7d' ? 6 : 29))
  return [start, end]
}
function observations(from: Date, to: Date, site: string): Session[] {
  const sessions: Session[] = []
  // Demo mode intentionally caps ranges; live mode delegates range limits to the API.
  if (
    !Number.isFinite(+from) ||
    !Number.isFinite(+to) ||
    +to <= +from ||
    +to - +from > DAY * 370
  )
    throw new Error('Demo mode supports valid date ranges of up to 370 days.')
  for (let timestamp = +from; timestamp < +to; timestamp += DAY) {
    if (timestamp > Date.now()) break
    const day = date(new Date(timestamp))
    const seed =
      Math.imul(Math.floor(timestamp / DAY), 2654435761) +
      [...site].reduce((sum, char) => sum + char.charCodeAt(0), 0)
    const random = seeded(seed ^ (seed >>> 16))
    const count = Math.floor(170 + random() * 150)
    for (let i = 0; i < count; i++) {
      const pick = <T>(values: T[]) =>
        values[Math.floor(random() * values.length)]
      const source = pick(sources)
      const device = pick([
        'Desktop',
        'Desktop',
        'Desktop',
        'Mobile',
        'Mobile',
        'Tablet',
      ])
      const viewed = Array.from(
        { length: random() < 0.37 ? 1 : 2 + Math.floor(random() * 4) },
        () => pick(pages),
      )
      const goals =
        random() < 0.11
          ? ['signup', ...(random() < 0.36 ? ['purchase'] : [])]
          : random() < 0.14
            ? ['download']
            : []
      sessions.push({
        day,
        hour: Math.floor(random() * 24),
        id: `${day}-${i}`,
        visitor: `${day}-${i % Math.floor(count * 0.85)}`,
        dimensions: {
          source,
          country: pick(countries),
          device,
          browser: pick([
            'Chrome',
            'Chrome',
            'Chrome',
            'Safari',
            'Firefox',
            'Edge',
          ]),
          os:
            device === 'Desktop'
              ? pick(['Windows', 'macOS', 'Linux'])
              : pick(['Android', 'iOS']),
          channel:
            source === 'Direct'
              ? 'Direct'
              : ['Google', 'Bing'].includes(source)
                ? 'Organic Search'
                : source === 'Newsletter'
                  ? 'Email'
                  : source === 'ChatGPT'
                    ? 'AI Assistants'
                    : 'Referral',
          utm_campaign: source === 'Newsletter' ? 'October newsletter' : '',
          hostname: 'demo.zonetree.dev',
        },
        pages: viewed,
        goals,
        duration: viewed.length === 1 ? 0 : Math.floor(45 + random() * 380),
      })
    }
  }
  return sessions
}
function filter(sessions: Session[], expressions: string[]) {
  let result = sessions
  for (const expression of expressions) {
    const match = expression.match(/^([\w$]+)(==|!=|\^=|~=)(.*)$/)
    if (!match)
      throw new Error(
        'Demo mode supports is, is not, starts with, and contains filters. Connect your API for advanced expressions.',
      )
    const [, dimension, operator, value] = match
    const accepts = (actual: string) =>
      operator === '=='
        ? actual === value
        : operator === '!='
          ? actual !== value
          : operator === '^='
            ? actual.startsWith(value)
            : actual.includes(value)
    result = result.flatMap((session) => {
      if (dimension === 'page') {
        const matched = session.pages.filter(accepts)
        return matched.length ? [{ ...session, pages: matched }] : []
      }
      return accepts(session.dimensions[dimension] ?? '') ? [session] : []
    })
  }
  return result
}
const unique = (sessions: Session[]) =>
  new Set(sessions.map((item) => item.visitor)).size
function summary(sessions: Session[], scope: Scope): Summary {
  const pageViews = sessions.reduce((sum, item) => sum + item.pages.length, 0)
  const sessionMetrics = !scope.filters.some((item) =>
    /^(page|hostname)(==|!=|\^=|~=)/.test(item),
  )
  const botMetrics = scope.filters.every((item) =>
    /^(page|hostname)(==|!=|\^=|~=)/.test(item),
  )
  return {
    visitors: unique(sessions),
    pageViews,
    sessions: sessionMetrics ? sessions.length : null,
    bounceRate: sessionMetrics
      ? sessions.length
        ? sessions.filter(
            (item) => item.pages.length === 1 && !item.goals.length,
          ).length / sessions.length
        : 0
      : null,
    averageSessionDuration: sessionMetrics
      ? sessions.length
        ? sessions.reduce((sum, item) => sum + item.duration, 0) /
          sessions.length
        : 0
      : null,
    pageViewsPerSession: sessionMetrics
      ? sessions.length
        ? pageViews / sessions.length
        : 0
      : null,
    botHits: botMetrics ? Math.round(pageViews * 0.08) : null,
  }
}
export class DemoClient {
  async request<T>(
    endpoint: string,
    scope: Scope,
    signal: AbortSignal,
    extra: Record<string, string> = {},
    body?: Record<string, unknown>,
  ): Promise<T> {
    signal.throwIfAborted()
    const [from, to] = demoDates(scope)
    const base = { from: from.toISOString(), to: to.toISOString(), plan }
    const current = filter(observations(from, to, scope.site), scope.filters)
    const oldFrom = new Date(+from - (+to - +from))
    const oldTo = new Date(+from)
    if (scope.compare === 'previous-year') {
      oldFrom.setTime(+from)
      oldTo.setTime(+to)
      oldFrom.setUTCFullYear(oldFrom.getUTCFullYear() - 1)
      oldTo.setUTCFullYear(oldTo.getUTCFullYear() - 1)
    }
    const previous =
      scope.compare === 'none'
        ? []
        : filter(observations(oldFrom, oldTo, scope.site), scope.filters)
    const compared = (items: Session[], old: Session[]) => ({
      ...summary(items, scope),
      ...(scope.compare !== 'none' ? { previous: summary(old, scope) } : {}),
    })
    const report = <R>(rows: R[], paginate = true): Report<R> => ({
      ...base,
      totalRows: rows.length,
      rows: paginate
        ? rows.slice(
            Number(extra.offset || 0),
            Number(extra.offset || 0) + Number(extra.limit || 10),
          )
        : rows,
    })
    let response: unknown
    switch (endpoint) {
      case 'summary':
        response = {
          ...base,
          summary: compared(current, previous),
        } satisfies SummaryReport
        break
      case 'realtime':
        response = { visitors: 24, minutes: 5 } satisfies Realtime
        break
      case 'timeseries': {
        const rows: Point[] = []
        const hourly = extra.interval === 'hour'
        for (let time = +from; time < +to; time += hourly ? DAY / 24 : DAY) {
          const bucket = new Date(time)
          const day = date(bucket)
          const oldDay = date(new Date(+oldFrom + time - +from))
          rows.push({
            bucket: bucket.toISOString(),
            summary: compared(
              current.filter(
                (item) =>
                  item.day === day &&
                  (!hourly || item.hour === bucket.getUTCHours()),
              ),
              previous.filter(
                (item) =>
                  item.day === oldDay &&
                  (!hourly || item.hour === bucket.getUTCHours()),
              ),
            ),
          })
        }
        response = report(rows, false)
        break
      }
      case 'breakdown': {
        const dimension = extra.dimension || 'source'
        const values = [
          ...new Set(current.map((item) => item.dimensions[dimension] ?? '')),
        ]
        response = report(
          values
            .map((value) => {
              const items = current.filter(
                (item) => (item.dimensions[dimension] ?? '') === value,
              )
              const old = previous.filter(
                (item) => (item.dimensions[dimension] ?? '') === value,
              )
              const make = (group: Session[], all: Session[]): Breakdown => {
                const metrics = summary(group, scope)
                return {
                  value,
                  visitors: metrics.visitors,
                  pageViews: metrics.pageViews,
                  sessions: metrics.sessions,
                  bounceRate: metrics.bounceRate,
                  averageSessionDuration: metrics.averageSessionDuration,
                  visitorShare: unique(all) ? unique(group) / unique(all) : 0,
                }
              }
              return {
                ...make(items, current),
                ...(scope.compare !== 'none'
                  ? { previous: make(old, previous) }
                  : {}),
              }
            })
            .sort((a, b) => b.visitors - a.visitors),
        )
        break
      }
      case 'pages':
      case 'exit-pages': {
        const values = [...new Set(current.flatMap((item) => item.pages))]
        const make = (page: string, all: Session[]): Page => {
          const group = all.filter((item) => item.pages.includes(page))
          const views = group.reduce(
            (sum, item) =>
              sum + item.pages.filter((value) => value === page).length,
            0,
          )
          const exits = group.filter(
            (item) => item.pages.at(-1) === page,
          ).length
          return {
            page,
            visitors: unique(group),
            pageViews: views,
            entrances: group.filter((item) => item.pages[0] === page).length,
            exits,
            exitRate: views ? exits / views : 0,
            botHits: summary(group, scope).botHits,
          }
        }
        response = report(
          values
            .map((page) => ({
              ...make(page, current),
              ...(scope.compare !== 'none'
                ? { previous: make(page, previous) }
                : {}),
            }))
            .sort((a, b) =>
              endpoint === 'exit-pages'
                ? (b.exits ?? 0) - (a.exits ?? 0)
                : b.pageViews - a.pageViews,
            ),
        )
        break
      }
      case 'goals':
        response = report(
          ['signup', 'download', 'purchase'].map((name) => {
            const group = current.filter((item) => item.goals.includes(name))
            return {
              name,
              events: group.length,
              visitors: unique(group),
              conversionRate: unique(current)
                ? unique(group) / unique(current)
                : 0,
            } satisfies Goal
          }),
        )
        break
      case 'bots': {
        if (
          scope.filters.some(
            (item) =>
              !/^(page|hostname|bot|bot_category)(==|!=|\^=|~=)/.test(item),
          )
        )
          throw new Error(
            'Bot reports only support page, hostname, bot, and bot_category filters. Remove audience filters to view bots.',
          )
        const total = summary(current, { ...scope, filters: [] }).botHits ?? 0
        response = report(
          [
            ['Googlebot', 'Search', 0.5],
            ['GPTBot', 'AI', 0.26],
            ['Bingbot', 'Search', 0.17],
            ['UptimeRobot', 'Monitoring', 0.07],
          ].map(
            ([name, category, share]) =>
              ({
                name: String(name),
                category: String(category),
                share: Number(share),
                hits: Math.round(total * Number(share)),
              }) satisfies Bot,
          ),
        )
        break
      }
      case 'query': {
        const event = String(body?.event || 'pageview')
        const groupBy = (body?.groupBy as string[]) || []
        const metrics = (body?.metrics as string[]) || ['visitors', 'pageviews']
        if (
          (body?.interval && body.interval !== 'none') ||
          body?.having ||
          body?.segments ||
          metrics.some(
            (key) =>
              ![
                'visitors',
                'pageviews',
                'events',
                'sessions',
                'bounce_rate',
                'avg_session_duration',
                'sum:revenue',
              ].includes(key),
          )
        )
          throw new Error(
            'Demo explorer supports totals and grouped visitors, pageviews, events, sessions, bounce_rate, and sum:revenue. Connect the API to use formulas, intervals, segments, or other metrics.',
          )
        const selected =
          event === 'pageview'
            ? current
            : current.filter((item) => item.goals.includes(event))
        const calculate = (items: Session[]): Record<string, number | null> => {
          const totals = summary(items, scope)
          return Object.fromEntries(
            metrics.map((key) => [
              key,
              key === 'visitors'
                ? totals.visitors
                : key === 'pageviews'
                  ? totals.pageViews
                  : key === 'events'
                    ? event === 'pageview'
                      ? totals.pageViews
                      : items.length
                    : key === 'sessions'
                      ? totals.sessions
                      : key === 'bounce_rate'
                        ? totals.bounceRate
                        : key === 'sum:revenue'
                          ? items.filter((item) =>
                              item.goals.includes('purchase'),
                            ).length * 49
                          : totals.averageSessionDuration,
            ]),
          )
        }
        const groups = new Map<string, Session[]>()
        selected.forEach((item) => {
          const key = JSON.stringify(
            groupBy.map((dimension) => item.dimensions[dimension] ?? null),
          )
          groups.set(key, [...(groups.get(key) || []), item])
        })
        const rows = [...groups.entries()].map(([key, items]) => ({
          dimensions: Object.fromEntries(
            groupBy.map((dimension, index) => [
              dimension,
              JSON.parse(key)[index],
            ]),
          ),
          values: calculate(items),
        }))
        response = {
          ...base,
          timeZone: 'UTC',
          interval: 'none',
          groupBy,
          metrics,
          totals: { values: calculate(selected) },
          totalRows: rows.length,
          rows: rows.slice(0, Number(body?.limit ?? 100)),
          plan: {
            ...plan,
            warnings: [
              'Demo query results are synthetic. Demo dates use UTC; live reports use your selected time zone.',
            ],
          },
        } satisfies QueryResult
        break
      }
      case 'funnel': {
        const steps = body?.steps as {
          name: string
          event: string
          filters?: string[]
        }[]
        if (!steps || steps.length < 2)
          throw new Error('Add at least two funnel steps.')
        let visitors: Session[] = current
        let first = 0,
          last = 0
        const rows = steps.map((step, index) => {
          visitors = filter(
            visitors.filter(
              (item) =>
                step.event === 'pageview' || item.goals.includes(step.event),
            ),
            step.filters || [],
          )
          const count = unique(visitors)
          if (index === 0) first = count
          const row = {
            name: step.name,
            visitors: count,
            conversionRate: first ? count / first : 0,
            stepConversionRate: index ? (last ? count / last : 0) : 1,
            dropOff: index ? last - count : 0,
          }
          last = count
          return row
        })
        response = {
          ...base,
          conversionRate: first ? last / first : 0,
          steps: rows,
          plan: {
            ...plan,
            warnings: [
              'Demo funnels illustrate conversion using synthetic sessions. Ordered events and the conversion window are evaluated by the live API.',
            ],
          },
        } satisfies FunnelResult
        break
      }
      default:
        throw new Error(`Unknown report: ${endpoint}`)
    }
    signal.throwIfAborted()
    return response as T
  }
}
