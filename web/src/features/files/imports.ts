import type { FileDependents, FileImports } from '@/lib/api'

/**
 * What both panels say about the strength of what they show. One sentence, said the same in both
 * places, because the two lists are the same evidence read in opposite directions and one of them
 * wording it more confidently than the other would be the one a reader believes (CONTEXT.md,
 * _Import_).
 */
export const IMPORT_EVIDENCE =
  'Read from import lines, not from a compiler, and a name resolves only where it names exactly one ' +
  'file here. Strong evidence, not proof.'

/**
 * What the imports panel has to say beside its list, or null when the list speaks for itself. The
 * three empty answers are three different facts — the extension was never read, the language has no
 * imports, the file writes none — and a panel that drew them alike would tell a reader a file
 * depends on nothing when what happened is that nothing looked.
 */
export function importsNote(file: FileImports): string | null {
  if (!file.profiled) {
    return `No language profile covers this extension, so this file’s import lines were never read. That is a different thing from it importing nothing.`
  }
  if (!file.hasImports) {
    return `${file.languageName} has no import concept, so a file in it names no other file to depend on. Nothing was extracted, and nothing was missed.`
  }
  if (file.imports.length === 0) {
    return 'This file imports nothing. Its language has import lines and none were written here.'
  }
  return file.capped ? `Only the first ${file.imports.length} are listed; the file has more.` : null
}

/**
 * What the dependents panel has to say beside its list. Every sentence here is about resolution
 * rather than about the code: an empty list is what no import line was found to name, a name several
 * files declare resolves to none of them, and an edge that could not be placed may well be a
 * dependency on this file. They accumulate rather than one winning, because a list cut off at the
 * ceiling on a file whose module is shared is thin for both reasons at once, and the busiest files
 * are where the reader can least afford to be told only half of it.
 */
export function dependentsNote(file: FileDependents): string | null {
  const notes: string[] = []
  if (file.dependents.length === 0) {
    notes.push('No import line in this project was found to name this file.')
  }
  if (file.capped) {
    notes.push(`Only the first ${file.dependents.length} are listed; more files import this one.`)
  }
  if (file.shareTheModule > 0) {
    notes.push(
      `This file declares ${file.module}, and ${file.shareTheModule} other file${pick(file.shareTheModule, '', 's')} here ${pick(file.shareTheModule, 'declares', 'declare')} it too. An import of that name therefore names no single file and could not appear in this list.`,
    )
  } else if (file.unplaced > 0) {
    notes.push(
      `${file.unplaced} unresolved import${pick(file.unplaced, '', 's')} in this project ${pick(file.unplaced, 'spells', 'spell')} this file’s name and could not be pointed at any file. A dependency on this one may be among them.`,
    )
  }
  return notes.length > 0 ? notes.join(' ') : null
}

/** Agreement for a count, for the noun and the verb alike: one helper, since it is one question. */
function pick(count: number, one: string, many: string) {
  return count === 1 ? one : many
}
