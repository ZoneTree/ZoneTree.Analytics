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

const utf8Escapes =
  /%[cd][0-9a-f]%[89ab][0-9a-f]|%e[0-9a-f](?:%[89ab][0-9a-f]){2}|%f[0-4](?:%[89ab][0-9a-f]){3}/gi
const unsafeLabelCharacters = /[\p{Cc}\p{Bidi_Control}\u2028\u2029]/gu

function decodePathCharacter(encoded: string): string {
  try {
    const character = decodeURIComponent(encoded)
    return character.search(unsafeLabelCharacters) < 0 ? character : encoded
  } catch {
    return encoded
  }
}

// Match the .NET PagePath formatter. Use the original value for report actions.
export function formatPagePath(path: string): string {
  const firstEscape = path.indexOf('%')
  if (firstEscape >= 0) {
    const suffix = path.search(/[?#]/)
    if (suffix < 0) path = path.replace(utf8Escapes, decodePathCharacter)
    else if (firstEscape < suffix)
      path =
        path.slice(0, suffix).replace(utf8Escapes, decodePathCharacter) +
        path.slice(suffix)
  }
  return path.replace(unsafeLabelCharacters, encodeURIComponent)
}

export function formatDimensionValue(dimension: string, value: string): string {
  return dimension === 'page' || dimension === 'entry_page'
    ? formatPagePath(value)
    : value
}

export function formatFilterLabel(filter: string): string {
  const prefix = /^(?:page|entry_page)(?:==|!=(?!~)|\^=|~=)/.exec(filter)?.[0]
  return prefix && !filter.includes('|')
    ? prefix + formatPagePath(filter.slice(prefix.length))
    : filter
}

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
