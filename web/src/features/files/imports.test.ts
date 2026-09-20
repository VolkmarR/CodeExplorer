import { expect, test } from 'vite-plus/test'
import { importsNote } from '@/features/files/imports'
import type { FileImports, ImportEdge } from '@/features/files/api'

const edge = (name: string, targetPath: string | null): ImportEdge => ({
  lineNumber: 1,
  name,
  targetPath,
  unresolved: targetPath === null ? 'nothing in this project declares this name' : null,
})

const imports = (fields: Partial<FileImports>): FileImports => ({
  capped: false,
  hasImports: true,
  imports: [],
  languageName: 'C#',
  module: null,
  profiled: true,
  qualifiedPath: 'one/src/Orders.cs',
  ...fields,
})

/**
 * The three ways an empty list means something other than "this file imports nothing", which is the
 * sentence the panel must never say by accident (CONTEXT.md, _Import_).
 */
test('an empty imports panel says which kind of empty it is', () => {
  expect(
    importsNote(imports({ hasImports: false, languageName: 'SQL', profiled: false })),
  ).toContain('No language profile covers')
  expect(importsNote(imports({ hasImports: false, languageName: 'SQL' }))).toContain(
    'SQL has no import concept',
  )
  expect(importsNote(imports({}))).toContain('imports nothing')
})

test('a file with imports has nothing to explain, and one cut off at the ceiling says so', () => {
  const some = imports({ imports: [edge('Orders.Domain', 'one/src/Orders.cs')] })

  expect(importsNote(some)).toBe(null)
  expect(importsNote({ ...some, capped: true })).toContain('first')
})

/**
 * An unresolved name is still a dependency, so it is listed as written with the reason beside it —
 * and the panel has nothing to add, because the row already says what happened to it.
 */
test('a name that did not resolve leaves the note empty, since the row itself says why', () => {
  expect(importsNote(imports({ imports: [edge('System.Text', null)] }))).toBe(null)
})
