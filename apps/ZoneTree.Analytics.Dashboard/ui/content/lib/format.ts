export const number = (value: number | null | undefined) =>
  value == null
    ? '—'
    : new Intl.NumberFormat('en', { maximumFractionDigits: 1 }).format(value)
export const percent = (value: number | null | undefined) =>
  value == null ? '—' : `${number(value * 100)}%`
export const duration = (value: number | null | undefined) =>
  value == null
    ? '—'
    : `${Math.floor(Math.round(value) / 60)}m ${Math.round(value) % 60}s`
export const change = (
  current: number | null | undefined,
  previous: number | null | undefined,
): number | null =>
  current == null || previous == null || previous === 0
    ? null
    : (current - previous) / previous
export function csv(rows: unknown[][]): string {
  return (
    '\uFEFF' +
    rows
      .map((row) =>
        row
          .map((value) => {
            let text = value == null ? '' : String(value)
            // Prevent spreadsheet formula injection from tracked paths and dimension values.
            if (typeof value === 'string' && /^[\s]*[=+@-]/.test(text))
              text = `'${text}`
            return `"${text.replace(/"/g, '""')}"`
          })
          .join(','),
      )
      .join('\r\n')
  )
}
export function filterExpression(
  dimension: string,
  operator: string,
  value: string,
): string {
  if (
    !/^[\w$]+$/.test(dimension) ||
    !['==', '!=', '^=', '~='].includes(operator)
  )
    throw new Error('Invalid filter.')
  // Commas separate filters in GET requests, pipes separate values in Filter.Parse.
  if (/[|,\r\n]/.test(value))
    throw new Error(
      'Use one filter value without commas, pipes, or line breaks.',
    )
  return `${dimension}${operator}${value}`
}
