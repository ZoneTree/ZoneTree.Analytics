import { readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { bumpVersion, versionPattern } from './version'

// Run from the repository root: yarn bump-version [delta]
const projectRoot = process.cwd()
if (process.argv.length > 3) {
  throw new Error('Usage: yarn bump-version [delta] (default: 1)')
}
const delta = process.argv[2] ?? '1'

// Both NuGet packages share VersionPrefix; package and assembly versions derive from it.
const versionSource = 'src/Directory.Build.props'
const versionDeclaration = /(<VersionPrefix>)([^<\r\n]*)(<\/VersionPrefix>)/g

// The private workspaces are not published, but carry the product version too.
const manifests = [
  'apps/ZoneTree.Analytics.Dashboard/ui/package.json',
  'docs-site/package.json',
]
const manifestVersion = /^(\s*"version"\s*:\s*")([^"\r\n]+)(")/gm

// Every file that declares the product version, with the pattern of its declaration.
const targets: { relativePath: string; pattern: RegExp }[] = [
  { relativePath: versionSource, pattern: versionDeclaration },
  ...manifests.map((relativePath) => ({
    relativePath,
    pattern: manifestVersion,
  })),
]

function read(relativePath: string) {
  return readFileSync(path.join(projectRoot, relativePath), 'utf8')
}

function declarations(relativePath: string, source: string, pattern: RegExp) {
  const matches = [...source.matchAll(pattern)]
  if (matches.length !== 1) {
    throw new Error(
      `Expected exactly one version declaration in ${relativePath}. No files have been changed.`,
    )
  }
  return matches[0]
}

const currentVersion = declarations(
  versionSource,
  read(versionSource),
  versionDeclaration,
)[2]
if (!versionPattern.test(currentVersion)) {
  throw new Error(
    `Invalid version ${currentVersion} in ${versionSource}. No files have been changed.`,
  )
}
const nextVersion = bumpVersion(currentVersion, delta)

type Update = { relativePath: string; source: string }
const updates: Update[] = []
for (const { relativePath, pattern } of targets) {
  const source = read(relativePath)
  const [, , version] = declarations(relativePath, source, pattern)
  if (version !== currentVersion) {
    throw new Error(
      `${relativePath} declares ${version}, but ${versionSource} declares ${currentVersion}. No files have been changed.`,
    )
  }
  const updated = source.replace(
    pattern,
    (_match, prefix: string, _version: string, suffix: string) =>
      `${prefix}${nextVersion}${suffix}`,
  )
  if (updated !== source) updates.push({ relativePath, source: updated })
}

// Read and validate every target before writing; keep formatting and line endings.
for (const update of updates) {
  writeFileSync(
    path.join(projectRoot, update.relativePath),
    update.source,
    'utf8',
  )
  console.log(`Updated ${update.relativePath}`)
}
console.log(`${currentVersion} -> ${nextVersion}: ${updates.length} file(s)`)
