import { expect, test } from 'vite-plus/test'
import { nextPreference, readPreference, resolveTheme } from '@/lib/theme'

/**
 * The preference outlives the tab and is read before React runs, so the two readers of it — the
 * inline script in index.html and this module — have to agree on every case a stored value can be
 * in, including the ones nobody wrote deliberately.
 */

test('a stored preference is read back, and anything else is the system', () => {
  expect(readPreference('dark')).toBe('dark')
  expect(readPreference('light')).toBe('light')
  expect(readPreference('system')).toBe('system')
  // A value from a future version, a truncated write, or a key nobody has set yet.
  expect(readPreference('midnight')).toBe('system')
  expect(readPreference('')).toBe('system')
  expect(readPreference(null)).toBe('system')
})

test('the system preference resolves to what the system asks for, and the others ignore it', () => {
  expect(resolveTheme('system', true)).toBe('dark')
  expect(resolveTheme('system', false)).toBe('light')
  expect(resolveTheme('dark', false)).toBe('dark')
  expect(resolveTheme('light', true)).toBe('light')
})

test('the toggle moves to the theme the reader is not looking at, and so leaves system behind', () => {
  // Explicit either way once clicked: a reader who has asked for light does not want the next
  // sunset to take it away again.
  expect(nextPreference('system', true)).toBe('light')
  expect(nextPreference('system', false)).toBe('dark')
  expect(nextPreference('dark', true)).toBe('light')
  expect(nextPreference('light', false)).toBe('dark')
})
