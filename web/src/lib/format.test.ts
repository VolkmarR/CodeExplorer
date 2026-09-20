import { expect, test } from 'vite-plus/test'
import {
  directoryOf,
  fileName,
  formatBytes,
  formatCount,
  shortSha,
  splitFileName,
} from '@/lib/format'

/**
 * `splitFileName`'s doc states an invariant rather than a behaviour: the highlighter and the file
 * rail ask what a file's extension is for different reasons and must not answer differently. An
 * invariant two callers rely on is worth pinning, because a change made for one of them looks
 * harmless from the other.
 */

test('a dotfile is all stem, because the whole name is the name', () => {
  expect(splitFileName('main/.gitignore')).toEqual({ extension: '', stem: '.gitignore' })
  expect(splitFileName('.editorconfig')).toEqual({ extension: '', stem: '.editorconfig' })
})

test('a name with no dot at all is all stem', () => {
  expect(splitFileName('main/Dockerfile')).toEqual({ extension: '', stem: 'Dockerfile' })
  expect(splitFileName('main/src/Makefile')).toEqual({ extension: '', stem: 'Makefile' })
  expect(splitFileName('README')).toEqual({ extension: '', stem: 'README' })
})

test('the extension is the last dot, not the first', () => {
  expect(splitFileName('main/src/App.test.tsx')).toEqual({ extension: 'tsx', stem: 'App.test' })
  expect(splitFileName('main/Program.cs')).toEqual({ extension: 'cs', stem: 'Program' })
  // A dotfile that does have an extension keeps the leading dot on its stem.
  expect(splitFileName('main/.eslintrc.json')).toEqual({ extension: 'json', stem: '.eslintrc' })
})

test('dots in a directory are not the file name`s', () => {
  expect(splitFileName('main/src.old/Program')).toEqual({ extension: '', stem: 'Program' })
})

test('a name ending in a dot has an empty extension rather than swallowing the dot', () => {
  expect(splitFileName('main/odd.')).toEqual({ extension: '', stem: 'odd' })
})

test('the file name is the last segment, and the directory is everything before it', () => {
  expect(fileName('main/src/Api/Program.cs')).toBe('Program.cs')
  expect(directoryOf('main/src/Api/Program.cs')).toBe('main/src/Api/')
  // The two are a split of the same string: joined, they are the path again.
  const path = 'main/src/Api/Program.cs'
  expect(directoryOf(path) + fileName(path)).toBe(path)
})

test('a path with no directory is all file name', () => {
  expect(fileName('Program.cs')).toBe('Program.cs')
  expect(directoryOf('Program.cs')).toBe('')
})

test('a sha is shown as the seven characters git itself abbreviates to', () => {
  expect(shortSha('a1b2c3d4e5f60718293a4b5c6d7e8f9012345678')).toBe('a1b2c3d')
  // Shorter than seven is left alone rather than padded.
  expect(shortSha('abc')).toBe('abc')
})

test('a size crosses its unit at the binary boundary and not before', () => {
  expect(formatBytes(0)).toBe('0 B')
  expect(formatBytes(1023)).toBe('1023 B')
  expect(formatBytes(1024)).toBe('1.0 KiB')
  expect(formatBytes(1024 * 1024 - 1)).toBe('1024.0 KiB')
  expect(formatBytes(1024 * 1024)).toBe('1.0 MiB')
})

test('a count is grouped, so six figures read as six figures', () => {
  expect(formatCount(1234).replaceAll(/[\s.,]/g, '')).toBe('1234')
  expect(formatCount(42)).toBe('42')
})
