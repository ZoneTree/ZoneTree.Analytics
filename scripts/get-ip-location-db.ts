import { mkdir, rename, writeFile } from 'node:fs/promises'
import path from 'node:path'

// Run from the repository root: yarn get-ip-location-db [output file]
// Downloads the latest ip-location-db user-country database (PDDL, updated daily) to where the
// dashboard loads it at startup. The output file is replaced only by an MMDB file.
const url =
  'https://github.com/sapics/ip-location-db/releases/download/latest/user-country.mmdb'
const defaultOutput =
  'apps/ZoneTree.Analytics.Dashboard/server/data/ip-location/user-country.mmdb'

if (process.argv.length > 3) {
  throw new Error('Usage: yarn get-ip-location-db [output file]')
}
const output = process.argv[2]
  ? path.resolve(process.argv[2])
  : path.resolve(import.meta.dirname, '..', defaultOutput)

const response = await fetch(url)
if (!response.ok) {
  throw new Error(
    `Downloading ${url} failed: ${response.status} ${response.statusText}`,
  )
}
const file = Buffer.from(await response.arrayBuffer())
// An MMDB file ends with its metadata, which follows this marker.
const marker = Buffer.from([0xab, 0xcd, 0xef, ...Buffer.from('MaxMind.com')])
if (file.subarray(-128 * 1024).lastIndexOf(marker) < 0) {
  throw new Error(`${url} did not return an MMDB file. Nothing was saved.`)
}
await mkdir(path.dirname(output), { recursive: true })
const download = `${output}.download`
await writeFile(download, file)
await rename(download, output)
const size = (file.length / 1024 / 1024).toFixed(1)
const updated = response.headers.get('last-modified') ?? 'unknown'
console.log(
  `Saved ${size} MiB to ${path.relative(process.cwd(), output)} (published ${updated}).`,
)
