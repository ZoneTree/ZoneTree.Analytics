import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { test } from 'vitest'

const root = fileURLToPath(new URL('../', import.meta.url))
const script = fileURLToPath(
  new URL('./check-dependencies.ts', import.meta.url),
)
const loader = import.meta.resolve('tsx')
const dependency = '@zonetree-dependency-check/shared'

function fixture(run: (directory: string) => void) {
  const temporaryRoot = path.join(root, '.tmp')
  mkdirSync(temporaryRoot, { recursive: true })
  const directory = mkdtempSync(path.join(temporaryRoot, 'dependency-check-'))
  try {
    manifest(directory, { name: 'fixture-repo', workspaces: ['packages/*'] })
    for (const name of ['first', 'second']) {
      manifest(path.join(directory, 'packages', name), {
        name,
        dependencies: { [dependency]: '^1.0.0' },
      })
    }
    run(directory)
  } finally {
    assert.equal(path.dirname(directory), temporaryRoot)
    assert(path.basename(directory).startsWith('dependency-check-'))
    rmSync(directory, { recursive: true, force: true })
  }
}

function manifest(directory: string, value: object) {
  mkdirSync(directory, { recursive: true })
  writeFileSync(path.join(directory, 'package.json'), JSON.stringify(value))
}

function install(directory: string) {
  // An import-only package with a hidden package.json still needs checking.
  manifest(path.join(directory, 'node_modules', dependency), {
    name: dependency,
    version: '1.1.0',
    type: 'module',
    exports: { '.': { import: './index.js' } },
  })
}

function check(directory: string) {
  const result = spawnSync(process.execPath, ['--import', loader, script], {
    cwd: directory,
    encoding: 'utf8',
  })
  assert.ifError(result.error)
  return { status: result.status, output: result.stdout + result.stderr }
}

test('shared dependencies resolve to one copy, including import-only packages', () => {
  fixture((directory) => {
    install(directory)
    const result = check(directory)
    assert.equal(result.status, 0, result.output)
    assert.match(result.output, /1 shared dependencies/)
  })
})

test('different declarations fail even when they resolve to the same package', () => {
  fixture((directory) => {
    install(directory)
    manifest(path.join(directory, 'packages/second'), {
      name: 'second',
      dependencies: { [dependency]: '^1.1.0' },
    })
    const result = check(directory)
    assert.equal(result.status, 1, result.output)
    assert.match(result.output, /Dependency declarations do not match/)
  })
})

test('separate installed copies fail even with identical versions', () => {
  fixture((directory) => {
    install(directory)
    install(path.join(directory, 'packages/second'))
    const result = check(directory)
    assert.equal(result.status, 1, result.output)
    assert.match(
      result.output,
      /different installed copies, even if their versions match/,
    )
  })
})

test('missing required dependencies fail', () => {
  fixture((directory) => {
    const result = check(directory)
    assert.equal(result.status, 1, result.output)
    assert.match(result.output, /dependency is not installed/)
  })
})

test('missing optional dependencies are allowed', () => {
  fixture((directory) => {
    for (const name of ['first', 'second']) {
      manifest(path.join(directory, 'packages', name), {
        name,
        optionalDependencies: { [dependency]: '^1.0.0' },
      })
    }
    const result = check(directory)
    assert.equal(result.status, 0, result.output)
  })
})

test('intentional differences require an explicit nonempty reason', () => {
  fixture((directory) => {
    const setException = (reason: string) =>
      manifest(directory, {
        name: 'fixture-repo',
        workspaces: ['packages/*'],
        dependencyConsistency: { exceptions: { [dependency]: reason } },
      })
    setException(' ')
    assert.equal(check(directory).status, 1)
    setException('This fixture intentionally omits the shared dependency.')
    const result = check(directory)
    assert.equal(result.status, 0, result.output)
    assert.match(result.output, /Explicit dependency exceptions/)
  })
})
