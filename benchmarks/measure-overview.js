// Runs in the browser page of query-benchmark.ts. A plain script, so the TypeScript
// loader cannot add helpers that the page does not have.
globalThis.measureOverview = async ({
  urls,
  samples,
  warmupBatches,
  maxConcurrentRequests,
}) => {
  // One overview load: each request is timed from its slot through JSON parsing.
  async function load() {
    const start = performance.now()
    const responses = []
    let next = 0
    async function slot() {
      while (next < urls.length) {
        const index = next++
        const requested = performance.now()
        const response = await fetch(urls[index], { cache: 'no-store' })
        const json = await response.json()
        responses[index] = {
          ms: performance.now() - requested,
          status: response.status,
          json,
        }
      }
    }
    await Promise.all(Array.from({ length: maxConcurrentRequests }, slot))
    return { ms: performance.now() - start, responses }
  }

  const discarded = []
  for (let i = 0; i < warmupBatches; ++i) discarded.push((await load()).ms)
  const loads = []
  for (let i = 0; i < samples; ++i) loads.push(await load())
  const batches = loads.map((x) => ({
    ms: x.ms,
    requests: x.responses.map((y) => ({ ms: y.ms, status: y.status })),
  }))
  const shapes = loads[loads.length - 1].responses.map(({ json }) => ({
    from: json.from,
    to: json.to,
    plan: json.plan,
    summary: json.summary,
    rows: Array.isArray(json.rows) ? json.rows.length : 0,
    totalRows: json.totalRows,
    serializedResponseCharacters: JSON.stringify(json).length,
  }))
  return { discarded, batches, shapes }
}
