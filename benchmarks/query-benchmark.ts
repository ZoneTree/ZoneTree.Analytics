import { mkdirSync, writeFileSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

// Measures the dashboard's annual overview reports over the latest ingestion benchmark.
// Start the dashboard in Release (yarn dashboard:release), run a benchmark from
// Tools → Benchmark, then run from the repository root:
//   yarn benchmark:queries [output.json] [origin]
// The output, artifacts/benchmarks/query-benchmark-results.json by default, has the layout of
// docs-site/content/docs/performance/query-benchmarks/results.json.
const output =
  process.argv[2] ?? 'artifacts/benchmarks/query-benchmark-results.json'
const origin = process.argv[3] ?? 'http://127.0.0.1:5088'
const samples = 30
const warmupBatches = 3
const maxConcurrentRequests = 3

const query = 'site=benchmark-results&tz=UTC'
const range = 'range=this-year&compare=previous-period'
const api = `${origin}/_analytics/benchmark/api`
const dashboardUrl = `${origin}/?${query}&${range}`

// The six requests of one overview load, as the dashboard sends them.
const overview: { name: string; path: string }[] = [
  { name: 'summary', path: `summary?${query}` },
  { name: 'timeseries', path: `timeseries?${query}&interval=day` },
  {
    name: 'pages',
    path: `pages?${query}&limit=5&includePageFlowMetrics=false`,
  },
  {
    name: 'breakdown:source',
    path: `breakdown?${query}&dimension=source&limit=5&includeSessionMetrics=false`,
  },
  {
    name: 'breakdown:country',
    path: `breakdown?${query}&dimension=country&limit=5&includeSessionMetrics=false`,
  },
  {
    name: 'breakdown:device',
    path: `breakdown?${query}&dimension=device&limit=10&includeSessionMetrics=false`,
  },
]

const scenarios = [
  { id: 'unfiltered', label: 'No filter', filter: '' },
  { id: 'source-github', label: 'source = GitHub', filter: 'source==GitHub' },
  { id: 'country-us', label: 'country = US', filter: 'country==US' },
  { id: 'page-blog', label: 'page = /blog', filter: 'page==/blog' },
]

const reportUrl = (path: string, filter: string) =>
  `${api}/${path}&${range}${filter ? `&filter=${encodeURIComponent(filter)}` : ''}`

interface Timing {
  median: number
  p95: number
  min: number
  max: number
}

// Median of the middle two samples; p95 by nearest rank.
function stats(values: number[]): Timing {
  const sorted = [...values].sort((a, b) => a - b)
  const middle = sorted.length / 2
  const median =
    sorted.length % 2
      ? sorted[Math.floor(middle)]
      : (sorted[middle - 1] + sorted[middle]) / 2
  return {
    median,
    p95: sorted[Math.ceil(0.95 * sorted.length) - 1],
    min: sorted[0],
    max: sorted[sorted.length - 1],
  }
}

// The result of measureOverview in measure-overview.js.
interface OverviewRun {
  discarded: number[]
  batches: { ms: number; requests: { ms: number; status: number }[] }[]
  shapes: {
    from: string
    to: string
    plan: unknown
    summary?: unknown
    rows: number
    totalRows?: number
    serializedResponseCharacters: number
  }[]
}

async function status() {
  const response = await fetch(`${origin}/_analytics/benchmark/status`)
  if (!response.ok)
    throw new Error(`The dashboard at ${origin} returned ${response.status}.`)
  return await response.json()
}

const before = await status()
if (before.benchmark?.state !== 'completed' || !before.benchmark.result)
  throw new Error('Run an ingestion benchmark in the dashboard first.')

const browser = await chromium.launch({ channel: 'chrome', headless: true })
const page = await browser.newPage()

// The dashboard's own first requests are recorded, not timed.
const initialReports: unknown[] = []
let capturing = true
page.on('response', async (response) => {
  const url = response.url()
  if (
    !capturing ||
    !url.includes('/_analytics/benchmark/api/') ||
    url.includes('/config')
  )
    return
  try {
    const json = await response.json()
    initialReports.push({
      url,
      status: response.status(),
      from: json.from,
      to: json.to,
      plan: json.plan,
    })
  } catch {
    // A response without a JSON body is not a report.
  }
})
await page.goto(dashboardUrl, { waitUntil: 'networkidle' })
await page.waitForTimeout(1000)
capturing = false
// Measure from a same-origin page, so dashboard refresh timers send no requests.
await page.goto(`${origin}/_analytics/benchmark/status`)
await page.addScriptTag({
  path: fileURLToPath(new URL('measure-overview.js', import.meta.url)),
})

const results = []
for (const scenario of scenarios) {
  const urls = overview.map((report) => reportUrl(report.path, scenario.filter))
  const run = (await page.evaluate(
    `measureOverview(${JSON.stringify({ urls, samples, warmupBatches, maxConcurrentRequests })})`,
  )) as OverviewRun

  const failedRequests = run.batches
    .flatMap((batch) => batch.requests)
    .filter((request) => request.status !== 200).length
  const batch = stats(run.batches.map((x) => x.ms))
  results.push({
    ...scenario,
    batch,
    failedRequests,
    reports: overview.map((report, index) => {
      const shape = run.shapes[index]
      return {
        name: report.name,
        url: urls[index],
        timing: stats(run.batches.map((x) => x.requests[index].ms)),
        plan: shape.plan,
        rows: shape.rows,
        ...(shape.summary ? { summary: shape.summary } : {}),
        ...(shape.totalRows !== undefined
          ? { totalRows: shape.totalRows }
          : {}),
        from: shape.from,
        to: shape.to,
        samples: run.batches.map((x) => x.requests[index].ms),
        serializedResponseCharacters: shape.serializedResponseCharacters,
      }
    }),
    batchSamples: run.batches.map((x) => x.ms),
    discardedWarmupBatches: run.discarded,
  })
  console.log(
    `${scenario.label}: ${batch.median.toFixed(1)} ms median, ${batch.p95.toFixed(1)} ms p95, ${failedRequests} failed`,
  )
}
const browserVersion = browser.version()
await browser.close()
const after = await status()

const measuredAt = new Date()
const dataset = before.benchmark.result
mkdirSync(path.dirname(output), { recursive: true })
writeFileSync(
  output,
  `${JSON.stringify(
    {
      measuredOn: measuredAt.toISOString().slice(0, 10),
      measuredAtUtc: measuredAt.toISOString(),
      browser: browserVersion,
      url: dashboardUrl,
      dataset,
      methodology: {
        samplesPerScenario: samples,
        discardedWarmupBatches: warmupBatches,
        requestsPerBatch: overview.length,
        maxConcurrentRequests,
        cache: 'no-store',
        metric: 'Browser fetch through JSON body parsing',
        percentiles: 'Median of middle two samples; p95 nearest rank',
        firstVisitIsCold: false,
        measuredRequests: samples * overview.length * scenarios.length,
        measuredBatches: samples * scenarios.length,
        initialNavigationExcluded: true,
        engineCaches: 'Warm; no host restart or cache clearing',
      },
      initialReports,
      scenarios: results,
      environment: {
        cpu: os.cpus()[0].model.trim(),
        logicalProcessors: os.cpus().length,
        operatingSystem: `${os.platform() === 'win32' ? 'Windows' : os.type()} ${os.arch()}`,
        hostBuild: dataset.build,
        transport: 'HTTP over loopback',
        storageProfile: 'Not recorded',
        installedMemoryGiB: Math.round(os.totalmem() / 2 ** 30),
      },
      stateUnchanged:
        JSON.stringify(before.benchmark) === JSON.stringify(after.benchmark) &&
        before.state === after.state,
    },
    null,
    2,
  )}\n`,
)
console.log(`Wrote ${output}`)
