import assert from 'node:assert/strict'
import { execFile } from 'node:child_process'
import {
  cp,
  mkdtemp,
  readdir,
  readFile,
  rm,
  stat,
  writeFile,
} from 'node:fs/promises'
import { basename, dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import { test } from 'vitest'
import { dashboardTrackerPath } from '../content/lib/contracts'

test('dashboard static build supports a base path and tree-shaken icons', async () => {
  const workspace = resolve(fileURLToPath(new URL('../', import.meta.url)))
  const content = await mkdtemp(resolve(workspace, '.qa-subpath-'))
  // Validate the immutable cleanup target before running the test.
  if (
    dirname(content) !== workspace ||
    !basename(content).startsWith('.qa-subpath-')
  )
    throw new Error(
      'Refusing to remove a directory outside the test workspace.',
    )
  try {
    await cp(resolve(workspace, 'content'), content, { recursive: true })
    const config = JSON.parse(
      await readFile(resolve(content, 'siteConfig.json'), 'utf8'),
    )
    config.basePath = '/analytics/'
    config.outDir = '../test-results/subpath-site'
    config.publishDir = '../test-results/subpath-publish'
    await writeFile(
      resolve(content, 'siteConfig.json'),
      JSON.stringify(config),
      'utf8',
    )
    const cli = resolve(
      dirname(fileURLToPath(import.meta.resolve('purestack'))),
      'cli.mjs',
    )
    await promisify(execFile)(
      process.execPath,
      [cli, 'build', '--content', content, '--clean'],
      { cwd: workspace },
    )
    const html = await readFile(
      resolve(workspace, 'test-results/subpath-site/pages/index.html'),
      'utf8',
    )
    assert.match(html, /href=["']?\/analytics\/pages\//)
    assert.match(html, /href=["']?\/analytics\/assets\/site\./)
    assert.match(html, /src=["']?\/analytics\/pages\/pages\./)
    assert.match(html, /data-base-path=["']?\/analytics/)
    const tracker = html.match(
      /<script\b[^>]*\bid=["']?zonetree-tracker["']?[^>]*>/,
    )?.[0]
    assert.ok(tracker, 'Every page includes the tracker')
    const source = tracker.match(/\bsrc=(?:"([^"]*)"|'([^']*)'|([^\s>]+))/)
    const trackerSrc = source?.[1] ?? source?.[2] ?? source?.[3]
    assert.ok(trackerSrc)
    assert.equal(
      new URL(trackerSrc, 'http://localhost/analytics/pages/').pathname,
      dashboardTrackerPath,
      'Tracker URL stays on the host API when the dashboard has a base path',
    )
    await assert.rejects(
      readFile(
        resolve(workspace, 'test-results/subpath-site/assets/config.json'),
      ),
      { code: 'ENOENT' },
    )
    // Development bundles are unminified, so allow headroom while still rejecting
    // accidental imports of the entire multi-megabyte SVG catalog.
    for (const file of await readdir(
      resolve(workspace, 'test-results/subpath-site'),
      {
        recursive: true,
      },
    )) {
      if (file.endsWith('.js'))
        assert.ok(
          (await stat(resolve(workspace, 'test-results/subpath-site', file)))
            .size < 750_000,
          `${file} must not include the full icon catalog`,
        )
    }
    await assert.rejects(
      readFile(
        resolve(
          workspace,
          'test-results/subpath-site/assets/dashboard-icons.json',
        ),
      ),
      { code: 'ENOENT' },
    )
    console.log(
      'PureStack subpath checks passed: navigation, styles, entry bundle, config base path, tree-shaken icons, bundle size.',
    )
  } finally {
    await rm(content, { recursive: true, force: true })
  }
})
