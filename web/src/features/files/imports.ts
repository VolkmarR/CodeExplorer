import type { FileImports } from '@/features/files/api'

/**
 * What the panel says about the strength of what it shows: an import line is text, a text profile is
 * what read it, and a name that resolved did so against what another file declared itself to be
 * (CONTEXT.md, _Import_).
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
