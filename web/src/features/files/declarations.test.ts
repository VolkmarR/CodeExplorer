import { expect, test } from 'vite-plus/test'
import { declarationsNote } from '@/features/files/declarations'
import type { Declaration, FileDeclarations } from '@/features/files/api'

const declaration = (member: string): Declaration => ({
  evidence: 'Text',
  lineNumber: 12,
  member,
  role: null,
  text: `    public void ${member}()`,
  type: null,
})

const declared = (fields: Partial<FileDeclarations>): FileDeclarations => ({
  capped: false,
  coverage: 'Read',
  declarations: [],
  languageName: 'C#',
  qualifiedPath: 'one/src/Orders.cs',
  skipReason: null,
  ...fields,
})

/**
 * The three ways an empty list means something other than "this file declares nothing", which is
 * the sentence the panel must never say by accident (CONTEXT.md, _Declaration_). They send a reader
 * to three different places: widen the profile, accept that the language has no answer here, or
 * believe the file.
 */
test('an empty declarations panel says which kind of empty it is', () => {
  expect(declarationsNote(declared({ coverage: 'Unprofiled', languageName: '.rst' }))).toContain(
    'No language profile covers',
  )
  expect(declarationsNote(declared({ coverage: 'Unreadable', languageName: 'CSS' }))).toContain(
    'CSS declarations',
  )
  expect(declarationsNote(declared({}))).toContain('declares nothing')
})

/**
 * A file the build skipped was never read, whatever its extension, so the note names why and claims
 * no reading (#370). The rail does not ask for such a file today; the server can still answer it.
 */
test('a skipped file is not indexed, and nothing was read from it', () => {
  const note = declarationsNote(declared({ coverage: 'Skipped', skipReason: 'binary' }))

  expect(note).toContain('not indexed (binary)')
  expect(note).toContain('nothing was read')
  expect(note).not.toContain('declares nothing')
})

test('a file with declarations has nothing to explain, and one cut short says so', () => {
  const some = declared({ declarations: [declaration('Place')] })

  expect(declarationsNote(some)).toBe(null)
  expect(declarationsNote({ ...some, capped: true })).toContain('first')
})
