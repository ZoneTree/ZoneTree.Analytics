import assert from 'node:assert/strict'
import { test } from 'vitest'
import { safeReturnUrl } from '../content/lib/session'

const origin = 'https://analytics.example.com'

test('sign-in returns only to pages of this dashboard', () => {
  assert.equal(
    safeReturnUrl('/pages/?site=shop#top', origin),
    '/pages/?site=shop#top',
  )
  assert.equal(safeReturnUrl('/admin/users/', origin), '/admin/users/')
  for (const unsafe of [
    null,
    '',
    'https://evil.example/',
    '//evil.example',
    '/\\evil.example',
    '/\t/evil.example',
    '/\n/evil.example',
    '/\r\n/evil.example',
    'javascript:alert(1)',
    '/signin/?returnUrl=/admin/',
    '/signout/',
  ])
    assert.equal(safeReturnUrl(unsafe, origin), '/', JSON.stringify(unsafe))
})
