import assert from 'node:assert/strict'
import { test } from 'vitest'
import type { Point, Summary } from '../content/lib/contracts'
import {
  chartPointBudget,
  MAX_CHART_POINTS,
  sampleChartIndices,
} from '../content/overview/chart-sampling'
import { prepareTrafficSeries } from '../content/overview/traffic'

test('small series keep every point; empty, singleton and two-point series are safe', () => {
  for (const rows of [[], [3], [3, 9], [3, 2, 5, 9]]) {
    assert.deepEqual(
      sampleChartIndices(rows, 20, 1, (row) => row),
      rows.map((_, i) => i),
    )
  }
  assert.deepEqual(
    sampleChartIndices([1, 2, 3, 4], 2, 1, (row) => row),
    [0, 3],
  )
})

test('million-point input stays bounded and retains extrema from both series in time order', () => {
  const rows = Array.from({ length: 1_000_000 }, (_, i) => i)
  const value = (i: number, series: number) =>
    series === 0
      ? i === 123_456
        ? 10_000
        : i === 456_789
          ? -10_000
          : Math.sin(i / 30)
      : i === 765_432
        ? 20_000
        : i === 876_543
          ? -20_000
          : Math.cos(i / 40)
  const selected = sampleChartIndices(rows, 350, 2, value)
  assert.ok(selected.length <= 350)
  assert.equal(selected[0], 0)
  assert.equal(selected.at(-1), rows.length - 1)
  for (const spike of [123_456, 456_789, 765_432, 876_543])
    assert.ok(selected.includes(spike))
  assert.ok(selected.every((index, i) => i === 0 || index > selected[i - 1]))
  assert.ok(
    sampleChartIndices(rows, 1_000_000, 1, (i) => i).length <= MAX_CHART_POINTS,
  )
})

test('sampling ignores non-finite values without manufacturing extrema; flat data remains bounded', () => {
  const rows = Array.from({ length: 10_000 }, (_, i) => i)
  const selected = sampleChartIndices(rows, 100, 1, (i) =>
    i === 500 ? 999 : i % 2 ? null : Number.NaN,
  )
  assert.ok(selected.includes(500))
  assert.ok(selected.length <= 100)
  assert.ok(sampleChartIndices(rows, 100, 1, () => 5).length <= 100)
})

test('point budget follows the visible SVG plot width and has a hard ceiling', () => {
  assert.ok(chartPointBudget(300, 263) < chartPointBudget(1000, 263))
  assert.equal(chartPointBudget(100_000, 100_000), MAX_CHART_POINTS)
  assert.ok(Number.isFinite(chartPointBudget(Number.NaN, 263)))
})

const summary = (bounceRate: number | null): Summary => ({
  visitors: 10,
  pageViews: 20,
  sessions: 12,
  bounceRate,
  averageSessionDuration: 30,
  pageViewsPerSession: 2,
  botHits: 0,
})
test('prepared series preserve actual time positions, rates and comparison alignment', () => {
  const start = Date.UTC(2026, 0, 1)
  const rows: Point[] = Array.from({ length: 10_000 }, (_, i) => ({
    bucket: new Date(start + i * i * 1000).toISOString(),
    summary: {
      ...summary(i === 1234 ? 0.99 : 0.1),
      previous: summary(i === 8765 ? 0.95 : 0.2),
    },
  }))
  const result = prepareTrafficSeries(
    rows,
    'bounceRate',
    'Bounce rate',
    true,
    true,
    'UTC',
    200,
  )
  assert.equal(result.series.length, 2)
  assert.deepEqual(result.positions[0], result.positions[1])
  const points = result.series[0].points as { value: number }[]
  const spike = points.findIndex((point) => point.value === 99)
  assert.ok(spike >= 0)
  assert.ok(
    Math.abs(result.positions[0][spike] - 1234 ** 2 / 9999 ** 2) < 1e-12,
  )
  assert.equal(result.positions[0][0], 0)
  assert.equal(result.positions[0].at(-1), 1)
  assert.ok(points.length <= 200)
  assert.ok(points.every((point) => point.value === 10 || point.value === 99))
})
