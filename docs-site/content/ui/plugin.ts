import { definePlugin } from 'purestack'
import { defineDocsComponents } from './components'
import { analyticsSkin } from './skin'
import { registerDocsStyles } from './styles'

export const analyticsDocsPlugin = definePlugin({
  name: 'analytics-docs',
  skins: { analytics: analyticsSkin },
  components: () => defineDocsComponents(),
  hooks: { onConfigResolved: () => registerDocsStyles() },
})

