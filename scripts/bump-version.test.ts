import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'vitest'

const root = fileURLToPath(new URL('../', import.meta.url))
const script = fileURLToPath(new URL('./bump-version.ts', import.meta.url))
const loader = import.meta.resolve('tsx')

const props = (versions: string) =>
  `<Project>\r\n  <PropertyGroup>\r\n${versions}    <IsPackable>false</IsPackable>\r\n  </PropertyGroup>\r\n</Project>\r\n`

const prefix = (version: string) =>
  `    <VersionPrefix>${version}</VersionPrefix>\r\n`

const manifest = (name: string, version: string) =>
  `{\n  "name": "${name}",\n  "version": "${version}",\n  "private": true\n}\n`

// The files the script edits, by their path relative to the repository root.
type Files = Record<string, string>

const versioned = (version: string, props: string = propsOf(version)) => ({
  'src/Directory.Build.props': props,
  'apps/ZoneTree.Analytics.Dashboard/ui/package.json': manifest(
    'zonetree-analytics-dashboard',
    version,
  ),
  'docs-site/package.json': manifest('analytics.zonetree.dev', version),
})

function propsOf(version: string) {
  return props(prefix(version))
}

function fixture(
  sources: Files,
  run: (
    read: () => Files,
    bump: (...args: string[]) => ReturnType<typeof spawnSync>,
  ) => void,
) {
  const temporaryRoot = path.join(root, '.tmp')
  mkdirSync(temporaryRoot, { recursive: true })
  const directory = mkdtempSync(path.join(temporaryRoot, 'bump-version-'))
  try {
    for (const [relativePath, source] of Object.entries(sources)) {
      const file = path.join(directory, relativePath)
      mkdirSync(path.dirname(file), { recursive: true })
      writeFileSync(file, source)
    }
    run(
      () =>
        Object.fromEntries(
          Object.keys(sources).map((relativePath) => [
            relativePath,
            readFileSync(path.join(directory, relativePath), 'utf8'),
          ]),
        ),
      (...args) =>
        spawnSync(process.execPath, ['--import', loader, script, ...args], {
          cwd: directory,
          encoding: 'utf8',
        }),
    )
  } finally {
    assert.equal(path.dirname(directory), temporaryRoot)
    assert(path.basename(directory).startsWith('bump-version-'))
    rmSync(directory, { recursive: true, force: true })
  }
}

test('bumps every version declaration and keeps the rest of each file', () => {
  fixture(versioned('1.0.0'), (read, bump) => {
    const first = bump()
    assert.equal(first.status, 0, String(first.stderr))
    assert.deepEqual(read(), versioned('1.0.1'))
    assert.match(String(first.stdout), /1\.0\.0 -> 1\.0\.1: 3 file\(s\)/)

    // A patch past 99 carries into the minor version.
    const carried = bump('100')
    assert.equal(carried.status, 0, String(carried.stderr))
    assert.deepEqual(read(), versioned('1.1.1'))
  })
})

test('changes nothing for an ambiguous or mismatched version or an invalid delta', () => {
  const mismatched = {
    ...versioned('1.0.0'),
    'docs-site/package.json': manifest('analytics.zonetree.dev', '0.1.0'),
  }
  for (const sources of [
    versioned('1.0.0', props(prefix('1.0.0') + prefix('1.0.0'))),
    mismatched,
  ]) {
    fixture(sources, (read, bump) => {
      const result = bump()
      assert.notEqual(result.status, 0)
      assert.match(String(result.stderr), /No files have been changed/)
      assert.deepEqual(read(), sources)
    })
  }
  const single = versioned('1.0.0')
  fixture(single, (read, bump) => {
    for (const delta of ['0', '-1', 'two']) {
      const result = bump(delta)
      assert.notEqual(result.status, 0, delta)
      assert.deepEqual(read(), single, delta)
    }
  })
})
