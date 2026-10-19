import { styleBuilder, themes } from '@purestack/ts-style'
import { registerDashboardLogoStyles } from './logo'
import { registerAccessibilityStyles } from './styles/accessibility'
import { registerAdminStyles } from './styles/admin'
import { registerBaseStyles } from './styles/base'
import { registerChartsStyles } from './styles/charts'
import { createDashboardStyleContext } from './styles/context'
import { registerControlsStyles } from './styles/controls'
import { registerDialogsStyles } from './styles/dialogs'
import { registerFormsStyles } from './styles/forms'
import { registerLayoutStyles } from './styles/layout'
import { registerNavigationStyles } from './styles/navigation'
import { registerReportsStyles } from './styles/reports'
import { registerResponsiveStyles } from './styles/responsive'
import { registerTablesStyles } from './styles/tables'

export function registerDashboardStyles() {
  registerDashboardLogoStyles()
  themes.forEach((theme, palette) => {
    const context = createDashboardStyleContext(
      styleBuilder.get(theme),
      theme,
      palette,
    )
    registerBaseStyles(context)
    registerNavigationStyles(context)
    registerLayoutStyles(context)
    registerControlsStyles(context)
    registerReportsStyles(context)
    registerChartsStyles(context)
    registerTablesStyles(context)
    registerFormsStyles(context)
    registerDialogsStyles(context)
    registerAdminStyles(context)
    // Visibility utilities override control display; breakpoints override them.
    registerAccessibilityStyles(context)
    registerResponsiveStyles(context)
  })
}
