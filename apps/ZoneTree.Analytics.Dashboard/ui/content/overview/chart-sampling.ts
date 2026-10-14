export const MAX_CHART_POINTS = 1536

// PureStack's line chart uses a 112 × 64 viewBox with an 86-unit plotting area.
export function chartPointBudget(width: number, height: number): number {
  const visibleWidth = (Math.min(width, (height * 112) / 64) * 86) / 112
  return Math.max(
    64,
    Math.min(
      MAX_CHART_POINTS,
      Number.isFinite(visibleWidth) ? Math.floor(visibleWidth) : 384,
    ),
  )
}

/** Keep endpoints and the extrema of every series without averaging report values. */
export function sampleChartIndices<T>(
  rows: readonly T[],
  budget: number,
  seriesCount: number,
  value: (row: T, series: number) => number | null,
): number[] {
  const limit = Math.max(
    2,
    Math.min(
      MAX_CHART_POINTS,
      Number.isFinite(budget) ? Math.floor(budget) : 384,
    ),
  )
  if (rows.length <= limit) return rows.map((_, index) => index)
  const buckets = Math.floor((limit - 2) / (2 * seriesCount))
  const selected = new Set([0, rows.length - 1])
  for (let bucket = 0; bucket < buckets; bucket++) {
    const start = 1 + Math.floor((bucket * (rows.length - 2)) / buckets)
    const end = 1 + Math.floor(((bucket + 1) * (rows.length - 2)) / buckets)
    for (let series = 0; series < seriesCount; series++) {
      let minIndex = -1
      let maxIndex = -1
      let min = Number.POSITIVE_INFINITY
      let max = Number.NEGATIVE_INFINITY
      for (let index = start; index < end; index++) {
        const current = value(rows[index], series)
        if (current == null || !Number.isFinite(current)) continue
        if (current < min) {
          min = current
          minIndex = index
        }
        if (current > max) {
          max = current
          maxIndex = index
        }
      }
      if (minIndex >= 0) selected.add(minIndex)
      if (maxIndex >= 0) selected.add(maxIndex)
    }
  }
  return [...selected].sort((a, b) => a - b)
}
