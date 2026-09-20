import { expect, test } from 'vite-plus/test'
import { declarationsNote } from '@/features/files/declarations'
import type { Declaration, FileDeclarations } from '@/features/files/api'

const declaration = (member: string): Declaration => ({
  evidence: 'text',
  lineNumber: 12,
  member,
  role: null,
  text: `    public void ${member}()`,
  type: null,
})

const declared = (fields: Partial<FileDeclarations>): FileDeclarations => ({
  capped: false,
  coverage: 'read',
  declarations: [],
  languageName: 'C#',
  qualifiedPath: 'one/src/Orders.cs',
  ...fields,
})

/**
 * The three ways an empty list means something other than "this file declares nothing", which is
 * the sentence the panel must never say by accident (CONTEXT.md, _Declaration_). They send a reader
 * to three different places: widen the profile, accept that the language has no answer here, or
 * believe the file.
 */
test('an empty declarations panel says which kind of empty it is', () => {
  expect(declarationsNote(declared({ coverage: 'unprofiled', languageName: '.rst' }))).toContain(
    'No language profile covers',
  )
  expect(declarationsNote(declared({ coverage: 'unreadable', languageName: 'CSS' }))).toContain(
    'CSS declarations',
  )
  expect(declarationsNote(declared({}))).toContain('declares nothing')
})

test('a file with declarations has nothing to explain, and one cut short says so', () => {
  const some = declared({ declarations: [declaration('Place')] })

  expect(declarationsNote(some)).toBe(null)
  expect(declarationsNote({ ...some, capped: true })).toContain('first')
})
