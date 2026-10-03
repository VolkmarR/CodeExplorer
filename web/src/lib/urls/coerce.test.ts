import { expect, test } from 'vite-plus/test'
import { flag, positiveInteger, text, textOrEmpty } from '@/lib/urls/coerce'

/**
 * The four readings every `validate*Search` is made of. A value arrives as a string from a pasted
 * URL and as itself from a typed link, so each case is asked both ways where the two differ.
 */

test('a positive integer is one from either side, and anything else is not there', () => {
  expect(positiveInteger('3')).toBe(3)
  expect(positiveInteger(7)).toBe(7)
  for (const value of ['0', '-2', '2.5', 'last', '', 0, 2.5, undefined, null]) {
    expect(positiveInteger(value), JSON.stringify(value)).toBe(undefined)
  }
})

test('text is a non-empty string, and the empty string is not there', () => {
  expect(text('main')).toBe('main')
  expect(text('')).toBe(undefined)
  expect(text(12)).toBe(undefined)
  expect(text(undefined)).toBe(undefined)
})

test('text or empty keeps the empty string, which some views read as a value', () => {
  expect(textOrEmpty('*.cs')).toBe('*.cs')
  expect(textOrEmpty('')).toBe('')
  expect(textOrEmpty(true)).toBe('')
  expect(textOrEmpty(undefined)).toBe('')
})

test('a flag is set by true or the word true, and by nothing else', () => {
  expect(flag(true)).toBe(true)
  expect(flag('true')).toBe(true)
  for (const value of ['false', 'yes', '1', 1, '', undefined]) {
    expect(flag(value), JSON.stringify(value)).toBe(false)
  }
})
