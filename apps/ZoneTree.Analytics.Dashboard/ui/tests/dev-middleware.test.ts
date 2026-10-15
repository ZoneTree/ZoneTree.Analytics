import assert from 'node:assert/strict'
import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { startDevServer } from 'purestack'
import { test } from 'vitest'
import {
  dashboardConfigPath,
  dashboardEventPath,
  dashboardTrackerPath,
} from '../content/lib/contracts'

test('standalone preview serves the repository tracker at the host root', async () => {
  const workspace = fileURLToPath(new URL('../', import.meta.url))
  const listener = createServer()
  await new Promise<void>((resolve) => listener.listen(0, '127.0.0.1', resolve))
  const address = listener.address()
  assert(address && typeof address !== 'string')
  const port = address.port
  await new Promise<void>((resolve, reject) =>
    listener.close((error) => (error ? reject(error) : resolve())),
  )

  const server = await startDevServer({
    host: '127.0.0.1',
    port,
    watch: false,
    liveReload: false,
    configFile: path.join(workspace, 'content/purestack.config.ts'),
    build: {
      siteConfig: {
        contentDir: path.join(workspace, 'content'),
        outDir: path.join(workspace, 'test-results/dev-middleware'),
        basePath: '/analytics/',
      },
    },
  })
  try {
    const base = `http://127.0.0.1:${port}`
    // Rendering waits for plugin initialization, as a browser visit does.
    const page = await fetch(`${base}/analytics/`)
    assert.equal(page.status, 200)
    await page.text()

    const tracker = await fetch(`${base}${dashboardTrackerPath}?v=2`)
    assert.equal(tracker.status, 200)
    assert.equal(tracker.headers.get('content-type'), 'application/javascript')
    assert.equal(tracker.headers.get('cache-control'), 'no-store')
    const expected = await readFile(
      new URL(
        '../../../../src/ZoneTree.Analytics.AspNetCore/tracker.js',
        import.meta.url,
      ),
    )
    const actual = Buffer.from(await tracker.arrayBuffer())
    assert.equal(
      createHash('sha256').update(actual).digest('hex'),
      createHash('sha256').update(expected).digest('hex'),
      'The preview must return the complete tracker source',
    )

    const config = await fetch(`${base}${dashboardConfigPath}`)
    assert.equal(config.status, 200)
    assert.equal((await config.json()).mode, 'demo')
    const beacon = await fetch(`${base}${dashboardEventPath}`, {
      method: 'POST',
      body: '{}',
    })
    assert.equal(beacon.status, 204)
  } finally {
    await server.close()
  }
})
