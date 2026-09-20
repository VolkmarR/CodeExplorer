import { expect, test } from 'vite-plus/test'
import { initials } from '@/features/auth/initials'

/**
 * A display name is whatever the tenant stores, so this is asserted on the shapes a tenant actually
 * returns rather than on a model of a person. The invariant worth pinning is that it always answers
 * something short: the avatar has room for two letters and no fallback behind this one.
 */

test('no name at all is a question mark, not an empty circle', () => {
  expect(initials(null)).toBe('?')
  expect(initials('')).toBe('?')
})

test('a written name gives the first letter of the first two words', () => {
  expect(initials('Volkmar Rigo')).toBe('VR')
  // A third word is dropped rather than making a third letter.
  expect(initials('Anna Maria Rossi')).toBe('AM')
})

test('a dotted account name is read as two words', () => {
  expect(initials('rigo.volkmar')).toBe('RV')
  expect(initials('anna_maria')).toBe('AM')
  expect(initials('jean-luc')).toBe('JL')
})

test('a UPN is read up to the domain, which is not part of who this is', () => {
  expect(initials('volkmar.rigo@infominds.eu')).toBe('VR')
  // With no separator before the `@`, the domain is the second word there is.
  expect(initials('vrigo@infominds.eu')).toBe('VI')
})

test('a single word gives one letter rather than padding to two', () => {
  expect(initials('Volkmar')).toBe('V')
  expect(initials('admin')).toBe('A')
})

test('separators alone, or around a name, do not become empty letters', () => {
  expect(initials('  Volkmar   Rigo  ')).toBe('VR')
  expect(initials('...')).toBe('')
  expect(initials('-')).toBe('')
})

test('the answer is never longer than the two letters the avatar has room for', () => {
  for (const name of [
    'Volkmar Rigo',
    'Anna Maria Rossi',
    'volkmar.rigo@infominds.eu',
    'a b c d e f',
    'Volkmar',
    '',
  ]) {
    expect(initials(name).length, name).toBeLessThanOrEqual(2)
  }
})
