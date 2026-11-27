import { redirectToSignIn } from '../session'

interface DashboardLiveUpdate {
  visitors: number | null
  reportsChanged: boolean
}

interface DashboardLiveContract {
  streams: {
    reports: { watch: (site: string) => DashboardLiveUpdate }
  }
}

export function watchLiveReports(
  url: string,
  site: string,
  update: (value: DashboardLiveUpdate) => void,
  unavailable: () => void,
): () => void {
  const lifetime = new AbortController()
  void observeLiveReports(
    url,
    site,
    update,
    unavailable,
    lifetime.signal,
  ).catch(() => {
    if (!lifetime.signal.aborted) unavailable()
  })
  return () => lifetime.abort()
}

async function observeLiveReports(
  url: string,
  site: string,
  update: (value: DashboardLiveUpdate) => void,
  unavailable: () => void,
  lifetime: AbortSignal,
) {
  const { CallError, connect } = await import('@puregate/web-socket')
  if (lifetime.aborted) return
  const connection = connect<DashboardLiveContract>(url, { streamBuffer: 1 })
  let reader: AbortController | null = null

  async function read(signal: AbortSignal) {
    try {
      for await (const value of connection.streams.reports.watch(site, {
        signal,
      })) {
        if (signal.aborted) return
        update(value)
      }
    } catch (error) {
      if (signal.aborted) return
      if (error instanceof CallError && error.code === 'session_ended') {
        redirectToSignIn()
        return
      }
    }
    if (!signal.aborted) unavailable()
  }

  const stopListening = connection.onStateChange((state) => {
    reader?.abort()
    reader = null
    if (state === 'open') {
      reader = new AbortController()
      void read(reader.signal)
    } else {
      unavailable()
    }
  })

  lifetime.addEventListener(
    'abort',
    () => {
      stopListening()
      reader?.abort()
      void connection.close()
    },
    { once: true },
  )
}
