import { expect, test } from 'vite-plus/test'
import { dependentsNote, importsNote } from '@/features/files/imports'
import type { FileDependents, FileImports, ImportEdge } from '@/features/files/api'

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

const dependents = (fields: Partial<FileDependents>): FileDependents => ({
  capped: false,
  dependents: [],
  module: null,
  qualifiedPath: 'one/src/Orders.cs',
  shareTheModule: 0,
  unplaced: 0,
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
 * The reverse lookup's two ways of being thinner than the project is. Both are about resolution and
 * neither is about the code, so an empty list that does not say so reads as "nothing depends on
 * this" — the one claim a dependency panel must never make by accident.
 */
test('a thin dependents list says why it is thin', () => {
  const shared = dependentsNote(dependents({ module: 'Orders.Storage', shareTheModule: 2 }))
  expect(shared).toContain('Orders.Storage')
  // The empty list is a sentence of its own, and it is about what was found rather than about the
  // code: "nothing depends on this" is the one claim this panel must never make by accident.
  expect(shared).toContain('No import line')

  expect(dependentsNote(dependents({ unplaced: 3 }))).toContain('3')
})

/**
 * A list at the ceiling on a file whose module is shared is thin for both reasons at once, and the
 * busiest file is where only being told half of it costs the reader most.
 */
test('a dependents list cut off at the ceiling says so as well as why it is thin', () => {
  const many = dependentsNote(
    dependents({
      capped: true,
      dependents: [{ lineNumber: 1, name: 'Orders.Domain', qualifiedPath: 'one/src/Report.cs' }],
      module: 'Orders.Storage',
      shareTheModule: 2,
    }),
  )

  expect(many).toContain('first')
  expect(many).toContain('Orders.Storage')
})

test('a list that is neither empty nor cut off has nothing to explain', () => {
  expect(
    dependentsNote(
      dependents({
        dependents: [{ lineNumber: 1, name: 'Orders.Domain', qualifiedPath: 'one/src/Report.cs' }],
      }),
    ),
  ).toBe(null)
})
