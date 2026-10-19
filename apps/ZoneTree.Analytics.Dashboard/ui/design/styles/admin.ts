import { sidebarColors as sidebar } from '../colors'
import type { DashboardStyleContext } from './context'

// Sign-in, setup, account and administration pages.
export function registerAdminStyles(context: DashboardStyleContext) {
  registerRoleStyles(context)
  registerAuthStyles(context)
  registerWizardStyles(context)
  registerSettingsStyles(context)
  registerAdminPageStyles(context)
  registerAdminResponsiveStyles(context)
}

// Navigation for roles appears once the session is known; the server enforces access anyway.
function registerRoleStyles({ root }: DashboardStyleContext) {
  root.select('.admin-only, .query-only').css({ display: 'none' })
  // The theme stylesheet is scoped to <html>, which carries the role classes.
  root
    .select(':scope.role-admin .admin-only, :scope.role-query .query-only')
    .css({ display: 'revert' })
  root
    .select(
      ':scope.role-admin .nav-list.admin-only, :scope.role-query .nav-list.query-only',
    )
    .css({ display: 'grid' })
  root
    .select(':scope.role-admin .nav-list a.admin-only')
    .css({ display: 'flex' })
  root
    .select(':scope.role-admin .sign-in__panel a.admin-only')
    .css({ display: 'inline-flex' })
  root.select('.topbar-account').css({ lineHeight: '0' })
  root.select('[data-user-initials]:empty::before').css({ content: "'•'" })
}

function registerAuthStyles({
  root,
  ink,
  muted,
  edge,
  surface,
  colors,
}: DashboardStyleContext) {
  root.select('body.zt-auth').css({ background: colors.canvas })
  root.select('.auth-shell').css({
    display: 'grid',
    gridTemplateColumns: 'minmax(300px, 420px) minmax(0, 1fr)',
    minHeight: '100dvh',
  })
  root.select('.auth-aside').css({
    background: sidebar.canvas,
    color: sidebar.ink,
    padding: '40px 36px',
    display: 'flex',
    flexDirection: 'column',
    gap: '48px',
    position: 'sticky',
    top: '0',
    height: '100dvh',
  })
  root.select('.auth-pitch').css({ marginTop: 'auto' })
  root.select('.auth-pitch h2').css({
    fontSize: '28px',
    lineHeight: '1.2',
    marginBottom: '12px',
    color: sidebar.ink,
  })
  root.select('.auth-pitch p').css({
    color: sidebar.link,
    fontSize: '13px',
    lineHeight: '1.6',
    maxWidth: '300px',
  })
  // The auth template has no topbar: the theme toggle sits in the corner on its own surface.
  root.select('.auth-actions').css({
    position: 'fixed',
    top: '16px',
    right: '16px',
    zIndex: '20',
    display: 'flex',
    background: surface,
    border: `1px solid ${edge}`,
    borderRadius: '999px',
    boxShadow: colors.shadow,
  })
  root.select('.auth-main').css({
    display: 'grid',
    placeItems: 'center',
    padding: '48px 24px',
    minWidth: '0',
  })
  root.select('.auth-card').css({
    width: 'min(440px, 100%)',
    background: surface,
    border: `1px solid ${edge}`,
    borderRadius: '14px',
    padding: '32px',
    boxShadow: colors.shadow,
    color: ink,
  })
  root.select('.auth-main-wide .auth-card').css({ width: 'min(760px, 100%)' })
  root.select('.auth-heading').css({ marginBottom: '24px' })
  root.select('.auth-heading h1').css({ fontSize: '24px', marginBottom: '8px' })
  root
    .select('.auth-heading p')
    .css({ color: muted, fontSize: '13px', lineHeight: '1.6' })
  root.select('.zt-dashboard .auth-form').css({ display: 'grid', gap: '16px' })
  root
    .select('.zt-dashboard .auth-form .form-block__field')
    .css({ margin: '0' })
  root.select('.auth-submit').css({ width: '100%', justifyContent: 'center' })
  root
    .select('.auth-footnote')
    .css({ marginTop: '24px', fontSize: '12px', color: muted })
  root.select('.zt-dashboard .auth-form .auth-submit').css({ marginTop: '4px' })
}

function registerWizardStyles({
  root,
  ink,
  muted,
  edge,
  surface,
  accent,
  colors,
}: DashboardStyleContext) {
  root
    .select('.setup-wizard')
    .css({ width: 'min(760px, 100%)', display: 'grid', gap: '20px' })
  root.select('.setup-card').css({ width: '100%' })
  root.select('.setup-steps').css({
    display: 'flex',
    flexWrap: 'wrap',
    gap: '6px 18px',
    listStyle: 'none',
    padding: '0',
    margin: '0',
    fontSize: '12px',
    color: muted,
  })
  root
    .select('.setup-steps li')
    .css({ display: 'flex', alignItems: 'center', gap: '8px' })
  root.select('.setup-step-number').css({
    width: '22px',
    height: '22px',
    borderRadius: '50%',
    display: 'grid',
    placeItems: 'center',
    border: `1px solid ${edge}`,
    background: surface,
    fontSize: '11px',
    fontWeight: '600',
  })
  root.select('.setup-steps li.current').css({ color: ink, fontWeight: '600' })
  root
    .select('.setup-steps li.current .setup-step-number')
    .css({ background: accent, borderColor: accent, color: colors.accentInk })
  root
    .select('.setup-steps li.complete .setup-step-number')
    .css({ background: colors.accentSoft, color: accent })
  root.select('.preset-grid').css({
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(190px, 1fr))',
    gap: '12px',
    margin: '4px 0 18px',
  })
  root.select('.preset-card').css({
    display: 'grid',
    gap: '6px',
    textAlign: 'left',
    padding: '16px',
    borderRadius: '10px',
    border: `1px solid ${edge}`,
    background: surface,
    color: ink,
    cursor: 'pointer',
    font: 'inherit',
  })
  root.select('.preset-card:hover').css({ borderColor: accent })
  root.select('.preset-card.selected').css({
    borderColor: accent,
    background: colors.accentSoft,
    boxShadow: `inset 0 0 0 1px ${accent}`,
  })
  root.select('.preset-card strong').css({ fontSize: '14px' })
  root
    .select('.preset-card span')
    .css({ fontSize: '12px', color: muted, lineHeight: '1.5' })
  root
    .select('.setup-summary')
    .css({ display: 'grid', gap: '10px', margin: '0 0 20px' })
  root.select('.setup-summary div, .detail-list div').css({
    display: 'grid',
    gridTemplateColumns: '160px minmax(0, 1fr)',
    gap: '12px',
    fontSize: '13px',
  })
  root.select('.setup-summary dt, .detail-list dt').css({ color: muted })
  root
    .select('.setup-summary dd, .detail-list dd')
    .css({ margin: '0', color: ink, overflowWrap: 'anywhere' })
  root
    .select('.detail-list dd small')
    .css({ display: 'block', color: muted, marginTop: '3px', fontSize: '11px' })
  root.select('.detail-list').css({ display: 'grid', gap: '12px', margin: '0' })
  root.select('.advanced-settings').css({
    borderTop: `1px solid ${edge}`,
    paddingTop: '16px',
    marginBottom: '16px',
  })
  root.select('.advanced-settings > summary').css({
    cursor: 'pointer',
    fontWeight: '600',
    color: ink,
    marginBottom: '12px',
  })
  root.select('.snippet').css({
    display: 'flex',
    gap: '12px',
    alignItems: 'center',
    padding: '12px 14px',
    borderRadius: '8px',
    border: `1px solid ${edge}`,
    background: colors.surfaceAlt,
    marginBottom: '8px',
  })
  root
    .select('.snippet code')
    .css({ flex: '1', overflowWrap: 'anywhere', fontSize: '12px' })
}

function registerSettingsStyles({
  root,
  ink,
  muted,
  edge,
  accent,
  colors,
}: DashboardStyleContext) {
  root.select('.settings-grid').css({
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '18px 22px',
    marginBottom: '8px',
  })
  root.select('.settings-group').css({
    gridColumn: '1 / -1',
    fontSize: '13px',
    fontWeight: '600',
    color: ink,
    margin: '14px 0 2px',
    paddingTop: '14px',
    borderTop: `1px solid ${edge}`,
  })
  root
    .select('.setting-field')
    .css({ display: 'grid', gap: '7px', alignContent: 'start', minWidth: '0' })
  root.select('.setting-label').css({
    display: 'flex',
    justifyContent: 'space-between',
    gap: '8px',
    fontSize: '12px',
    color: muted,
  })
  root.select('.setting-tier').css({
    fontSize: '10px',
    padding: '1px 7px',
    borderRadius: '999px',
    whiteSpace: 'nowrap',
    background: colors.hover,
    color: muted,
    alignSelf: 'start',
  })
  root
    .select('.tier-Reopen')
    .css({ background: colors.infoSoft, color: colors.info })
  root
    .select('.tier-Restart')
    .css({ background: colors.warningSoft, color: colors.warning })
  root
    .select('.tier-Fixed')
    .css({ background: colors.dangerSoft, color: colors.danger })
  root
    .select('.setting-input')
    .css({ display: 'flex', alignItems: 'center', gap: '8px' })
  root
    .select('.setting-unit')
    .css({ fontSize: '12px', color: muted, whiteSpace: 'nowrap' })
  root.select('.zt-dashboard .setting-field select').css({
    border: `1px solid ${edge}`,
    borderRadius: '6px',
    padding: '9px 11px',
    background: colors.surface,
    color: ink,
    width: '100%',
    minWidth: '0',
  })
  root.select('.zt-dashboard label.check').css({
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '12px',
    color: ink,
    cursor: 'pointer',
  })
  root
    .select('.zt-dashboard label.check input')
    .css({ width: 'auto', padding: '0', border: '0', accentColor: accent })
  root
    .select('.setting-choices')
    .css({ display: 'flex', flexWrap: 'wrap', gap: '8px 16px' })
  root.select('.setting-locked').css({ opacity: '0.75' })
  root.select('.advanced-toggle').css({ margin: '4px 0 16px' })
  root
    .select('.settings-help')
    .css({ fontSize: '12px', color: muted, marginBottom: '14px' })
  root.select('.settings-section').css({ marginTop: '22px' })
  root
    .select('.settings-section h2')
    .css({ fontSize: '16px', marginBottom: '4px' })
  root.select('.settings-layout').css({
    display: 'grid',
    gridTemplateColumns: '200px minmax(0, 1fr)',
    gap: '24px',
    alignItems: 'start',
  })
  root
    .select('.settings-nav')
    .css({ display: 'grid', gap: '2px', position: 'sticky', top: '24px' })
  root.select('.settings-nav button').css({
    textAlign: 'left',
    padding: '8px 12px',
    borderRadius: '6px',
    border: '0',
    background: 'transparent',
    color: muted,
    font: 'inherit',
    fontSize: '13px',
    cursor: 'pointer',
  })
  root
    .select('.settings-nav button:hover')
    .css({ background: colors.hover, color: ink })
  root
    .select('.settings-nav button.active')
    .css({ background: colors.accentSoft, color: accent, fontWeight: '600' })
  root.select('.sticky-actions').css({
    position: 'sticky',
    bottom: '0',
    display: 'flex',
    gap: '12px',
    padding: '14px 0',
    background: colors.canvas,
    zIndex: '5',
  })
  root.select('.save-review').css({ borderColor: accent })
  root.select('.change-list').css({
    listStyle: 'none',
    padding: '0',
    margin: '0 0 16px',
    display: 'grid',
    gap: '8px',
  })
  root.select('.change-list li').css({
    display: 'flex',
    justifyContent: 'space-between',
    gap: '12px',
    fontSize: '13px',
  })
}

function registerAdminPageStyles({
  root,
  ink,
  muted,
  edge,
  colors,
}: DashboardStyleContext) {
  root.select('.admin-columns').css({
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '22px',
    alignItems: 'start',
  })
  root.select('.status-cards').css({
    display: 'grid',
    gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
    gap: '16px',
    marginBottom: '22px',
  })
  root
    .select('.status-card .panel__body')
    .css({ display: 'grid', gap: '6px', padding: '18px' })
  root.select('.status-card span').css({ fontSize: '12px', color: muted })
  root.select('.status-card strong').css({
    fontSize: '22px',
    color: ink,
    fontWeight: '600',
    fontVariantNumeric: 'tabular-nums',
  })
  root.select('.status-card small').css({ fontSize: '11px', color: muted })
  root.select('.session-list').css({
    listStyle: 'none',
    padding: '0',
    margin: '0 0 12px',
    display: 'grid',
    gap: '2px',
  })
  root.select('.session-list li').css({
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: '12px',
    padding: '10px 0',
    borderBottom: `1px solid ${edge}`,
    fontSize: '13px',
  })
  root
    .select('.session-list small')
    .css({ display: 'block', color: muted, fontSize: '11px', marginTop: '3px' })
  root.select('.session-list .badge').css({ marginLeft: '8px' })
  root.select('.user-sessions').css({
    marginTop: '22px',
    paddingTop: '18px',
    borderTop: `1px solid ${edge}`,
  })
  root
    .select('.user-sessions h3')
    .css({ fontSize: '14px', marginBottom: '8px' })
  root
    .select('.users-table td small')
    .css({ display: 'block', color: muted, fontSize: '11px' })
  root
    .select('.users-table tr.disabled td, .audit-table tr.warning td')
    .css({ color: muted })
  root
    .select('.audit-table tr.warning td:nth-child(3)')
    .css({ color: colors.danger })
  root.select('.site-access').css({
    border: `1px solid ${edge}`,
    borderRadius: '8px',
    padding: '12px 14px',
    margin: '0 0 16px',
    display: 'grid',
    gap: '10px',
  })
  root
    .select('.site-access legend')
    .css({ fontSize: '12px', color: muted, padding: '0 6px' })
  root.select('.sites-editor').css({ display: 'grid', gap: '12px' })
  root.select('.site-row, .row-editor-row').css({
    display: 'grid',
    gridTemplateColumns:
      'minmax(0, 1fr) minmax(0, 0.8fr) minmax(0, 1.4fr) 34px',
    gap: '10px',
    alignItems: 'end',
  })
  root
    .select('.row-editor-row')
    .css({ gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr) 34px' })
  root
    .select('.row-editor')
    .css({ display: 'grid', gap: '10px', marginBottom: '12px' })
  root.select('.sites-options').css({ marginTop: '8px' })
  root.select('.button.danger').css({ color: colors.danger })
}

function registerAdminResponsiveStyles({ root }: DashboardStyleContext) {
  const tablet = root.media('max-width: 1150px')
  tablet
    .select('.status-cards')
    .css({ gridTemplateColumns: 'repeat(2, minmax(0, 1fr))' })
  tablet.select('.admin-columns').css({ gridTemplateColumns: 'minmax(0, 1fr)' })
  const narrow = root.media('max-width: 850px')
  narrow.select('.auth-shell').css({
    gridTemplateColumns: 'minmax(0, 1fr)',
    gridTemplateRows: 'auto 1fr',
  })
  narrow
    .select('.auth-aside')
    .css({ position: 'static', height: 'auto', padding: '24px', gap: '16px' })
  narrow
    .select('.auth-pitch, .auth-aside .privacy-mark')
    .css({ display: 'none' })
  narrow
    .select('.auth-main')
    .css({ padding: '24px 16px', placeItems: 'start center' })
  narrow.select('.auth-card').css({ padding: '22px' })
  narrow
    .select('.settings-grid, .settings-layout')
    .css({ gridTemplateColumns: 'minmax(0, 1fr)' })
  narrow
    .select('.settings-nav')
    .css({ position: 'static', display: 'flex', flexWrap: 'wrap' })
  narrow
    .select('.site-row, .row-editor-row')
    .css({ gridTemplateColumns: 'minmax(0, 1fr) 34px' })
  narrow
    .select(
      '.site-row label, .row-editor-row label, .row-editor-row .setting-choices',
    )
    .css({ gridColumn: '1' })
  narrow.select('.status-cards').css({ gridTemplateColumns: 'minmax(0, 1fr)' })
  narrow
    .select('.setup-summary div, .detail-list div')
    .css({ gridTemplateColumns: 'minmax(0, 1fr)', gap: '2px' })
}
