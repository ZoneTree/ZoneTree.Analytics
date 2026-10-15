import { type ChildProcess, execFile, spawn } from 'node:child_process'
import { mkdir, mkdtemp, rm } from 'node:fs/promises'
import { basename, dirname, join, resolve } from 'node:path'
import { setTimeout as pause } from 'node:timers/promises'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import { buildSite } from 'purestack'
import type { TestProject } from 'vitest/node'
import { dashboardPlugin } from '../design/plugin'

declare module 'vitest' {
  interface ProvidedContext {
    dashboardSiteRoot: string
    dashboardTestUrl: string
  }
}

export default async function setup(project: TestProject) {
  const workspace = resolve(fileURLToPath(new URL('../', import.meta.url)))
  const repo = resolve(workspace, '../../..')
  const results = join(workspace, 'test-results')
  await mkdir(results, { recursive: true })
  const temporary = await mkdtemp(join(results, 'vitest-'))
  if (
    dirname(temporary) !== results ||
    !basename(temporary).startsWith('vitest-')
  )
    throw new Error('Test host cleanup target must be inside test-results.')

  let host: ChildProcess | undefined
  let closed: Promise<void> | undefined
  const stopOnExit = () => host?.kill()
  const teardown = async () => {
    process.off('exit', stopOnExit)
    try {
      if (host?.pid && host.exitCode === null && host.signalCode === null) {
        host.kill()
        await Promise.race([closed, pause(5_000, undefined, { ref: false })])
        if (host.exitCode === null && host.signalCode === null)
          host.kill('SIGKILL')
      }
      await closed
    } finally {
      await rm(temporary, { recursive: true, force: true, maxRetries: 5 })
    }
  }

  try {
    const site = join(temporary, 'site')
    console.log('Building dashboard test assets...')
    await buildSite({
      siteConfig: {
        rootDir: workspace,
        contentDir: join(workspace, 'content'),
        outDir: site,
      },
      options: { plugins: [dashboardPlugin] },
    })
    project.provide('dashboardSiteRoot', site)

    if (process.env.ZT_DASHBOARD_TEST_URL) {
      const url = new URL(process.env.ZT_DASHBOARD_TEST_URL)
      project.provide('dashboardTestUrl', url.href)
      return teardown
    }

    const app = join(temporary, 'app')
    console.log('Building isolated .NET dashboard test host...')
    await promisify(execFile)(
      'dotnet',
      [
        'build',
        join(workspace, '../server/ZoneTree.Analytics.Dashboard.csproj'),
        '--configuration',
        'Release',
        '--artifacts-path',
        join(temporary, 'artifacts'),
        '--output',
        app,
        '--verbosity',
        'minimal',
      ],
      { cwd: repo, windowsHide: true, timeout: 120_000, maxBuffer: 4_194_304 },
    )

    host = spawn(
      'dotnet',
      [
        join(app, 'ZoneTree.Analytics.Dashboard.dll'),
        '--contentRoot',
        temporary,
        '--Dashboard:Watch=false',
        '--Dashboard:Path',
        site,
        '--Analytics:DataDirectory',
        join(temporary, 'data/analytics'),
        '--Urls=http://127.0.0.1:0',
        '--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information',
      ],
      {
        cwd: temporary,
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' },
      },
    )
    const child = host
    closed = new Promise<void>((resolve) =>
      child.once('close', () => resolve()),
    )
    process.once('exit', stopOnExit)
    let output = ''
    let startupError: Error | undefined
    const capture = (data: Buffer) => {
      output = (output + data.toString()).slice(-65_536)
    }
    child.stdout?.on('data', capture)
    child.stderr?.on('data', capture)
    child.on('error', (error) => {
      startupError = error
    })
    const deadline = Date.now() + 30_000
    while (Date.now() < deadline) {
      if (startupError) throw startupError
      if (child.exitCode !== null || child.signalCode !== null)
        throw new Error(`Dashboard test host exited before startup:\n${output}`)
      const url = output.match(
        /Now listening on: (http:\/\/127\.0\.0\.1:\d+)/,
      )?.[1]
      if (url) {
        project.provide('dashboardTestUrl', `${url}/`)
        console.log(`Dashboard test host ready at ${url}`)
        return teardown
      }
      await pause(100)
    }
    throw new Error(`Dashboard test host startup timed out:\n${output}`)
  } catch (error) {
    await teardown()
    throw error
  }
}
