import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  root: fileURLToPath(new URL('./', import.meta.url)),
  test: {
    environment: 'node',
    restoreMocks: true,
    unstubGlobals: true,
    testTimeout: 10_000,
    projects: [
      {
        test: {
          name: 'unit',
          include: [
            'scripts/**/*.test.ts',
            'apps/ZoneTree.Analytics.Dashboard/ui/tests/*.test.ts',
          ],
        },
      },
      {
        test: {
          name: 'integration',
          globalSetup: [
            './apps/ZoneTree.Analytics.Dashboard/ui/tests/setup.ts',
          ],
          include: [
            'apps/ZoneTree.Analytics.Dashboard/ui/tests/browser.ts',
            'apps/ZoneTree.Analytics.Dashboard/ui/tests/subpath.ts',
            'apps/ZoneTree.Analytics.Dashboard/ui/tests/tracker.ts',
          ],
          testTimeout: 120_000,
        },
      },
    ],
  },
})
