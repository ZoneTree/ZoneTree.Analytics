import { CallError, connect } from '@puregate/web-socket'
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

  return () => {
    stopListening()
    reader?.abort()
    void connection.close()
  }
}
