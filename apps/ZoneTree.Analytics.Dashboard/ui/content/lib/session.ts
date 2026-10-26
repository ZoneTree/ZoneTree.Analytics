import type { MeResponse } from './admin-contracts'
import type { SessionUser } from './contracts'

// PureStack's auth hint script shows the signed-in avatar while this key holds "1".
export const signedInHintKey = 'signed-in-hint'
const initialsKey = 'zt-dashboard-initials'
// The sidebar script in design/plugin.ts reads it to show the role's navigation before the first paint.
export const roleKey = 'zt-dashboard-role'
export const dashboardApiPath = '/_dashboard/api'

export function initials(name: string): string {
  const words = name
    .trim()
    .split(/[\s._@-]+/)
    .filter(Boolean)
  const letters =
    words.length > 1
      ? words[0][0] + words[words.length - 1][0]
      : name.trim().slice(0, 2)
  return letters.toUpperCase() || '?'
}

function store(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key)
    else localStorage.setItem(key, value)
  } catch {
    /* Private browsers may disable storage. */
  }
}

function read(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

function showInitials(text: string) {
  for (const element of document.querySelectorAll('[data-user-initials]'))
    element.textContent = text
}

// Shows who is signed in: the topbar avatar, and the navigation the role may use.
export function applyUser(user: SessionUser | null | undefined) {
  const root = document.documentElement
  root.classList.toggle('signed-in', !!user)
  root.classList.toggle('role-admin', user?.role === 'Administrator')
  root.classList.toggle(
    'role-query',
    user?.role === 'Administrator' || user?.role === 'Analyst',
  )
  store(signedInHintKey, user ? '1' : null)
  store(roleKey, user ? user.role : null)
  const text = user ? initials(user.displayName || user.userName) : ''
  store(initialsKey, user ? text : null)
  showInitials(text)
  revealCurrentLink()
}

// Without a saved sidebar position, the current page's link comes into view once the role's
// navigation is shown: before, it may have been hidden.
function revealCurrentLink() {
  try {
    if (sessionStorage.getItem('zt-sidebar-scroll') !== null) return
  } catch {
    return
  }
  document
    .querySelector('.sidebar [aria-current="page"]')
    ?.scrollIntoView({ block: 'nearest' })
}

export function redirectToSignIn() {
  applyUser(null)
  const here = location.pathname + location.search
  if (location.pathname.startsWith('/signin')) return
  location.assign(`/signin/?returnUrl=${encodeURIComponent(here)}`)
}

// Only same-site paths: an open redirect would let a link send users elsewhere after sign-in.
// The browser's own URL parser decides, because it drops tabs and newlines and reads
// backslashes as slashes: "/<tab>/evil.example" would otherwise become "//evil.example".
export function safeReturnUrl(
  value: string | null,
  origin = location.origin,
): string {
  if (!value?.startsWith('/')) return '/'
  let url: URL
  try {
    url = new URL(value, origin)
  } catch {
    return '/'
  }
  if (url.origin !== origin) return '/'
  const path = url.pathname + url.search + url.hash
  if (path.startsWith('/signin') || path.startsWith('/signout')) return '/'
  return path
}

export class DashboardApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public details: Record<string, unknown> = {},
  ) {
    super(message)
  }
}

export interface DashboardFetchOptions {
  method?: string
  body?: unknown
  // Sign-in and setup answer 401 for wrong credentials instead of an ended session.
  redirectOnUnauthorized?: boolean
  signal?: AbortSignal
}

export async function dashboardFetch<T>(
  path: string,
  options: DashboardFetchOptions = {},
): Promise<T> {
  let response: Response
  try {
    response = await fetch(`${dashboardApiPath}${path}`, {
      method: options.method ?? (options.body === undefined ? 'GET' : 'POST'),
      credentials: 'same-origin',
      cache: 'no-store',
      signal: options.signal,
      headers: {
        Accept: 'application/json',
        ...(options.body === undefined
          ? {}
          : { 'Content-Type': 'application/json' }),
      },
      body:
        options.body === undefined ? undefined : JSON.stringify(options.body),
    })
  } catch (error) {
    if ((error as Error).name === 'AbortError') throw error
    throw new DashboardApiError('Cannot reach the dashboard server.', 0)
  }
  if (response.status === 401 && options.redirectOnUnauthorized !== false) {
    redirectToSignIn()
    throw new DashboardApiError('Your session ended. Sign in again.', 401)
  }
  if (response.status === 204) return undefined as T
  const value = (await response.json().catch(() => null)) as Record<
    string,
    unknown
  > | null
  if (!response.ok)
    throw new DashboardApiError(
      typeof value?.error === 'string'
        ? value.error
        : response.status === 404
          ? 'This needs the ZoneTree Analytics server. The standalone preview has no dashboard API.'
          : `The dashboard server returned HTTP ${response.status}.`,
      response.status,
      value ?? {},
    )
  return value as T
}

export async function loadMe(): Promise<MeResponse> {
  const me = await dashboardFetch<MeResponse>('/auth/me')
  applyUser(me.user)
  return me
}

// Pages are static; the avatar initials arrive before the session request finishes.
if (typeof document !== 'undefined') {
  const remembered = read(initialsKey)
  if (remembered) showInitials(remembered)
}
