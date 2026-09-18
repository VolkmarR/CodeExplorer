import type { Declaration, FileDeclarations } from '@/lib/api'

/**
 * What the declarations panel has to say beside its list, or null when the list speaks for itself.
 * The three empty answers are three different facts — the extension was never covered, the language
 * has no declarations this can read, the file declares none — and a panel that drew them alike would
 * tell a reader a file declares nothing when what happened is that nothing looked.
 */
export function declarationsNote(file: FileDeclarations): string | null {
  // A switch on the one field the server sends rather than a cascade over two flags: the order of
  // the checks was itself load-bearing before, and getting it wrong printed a true sentence about
  // the wrong fact.
  switch (file.coverage) {
    case 'unprofiled':
      return `No language profile covers this extension, so this file was read with the conservative default shapes. A declaration form this does not know is one it did not find, not one that is not there.`
    case 'unreadable':
      return `${file.languageName} declarations are not something this can read from a line, so nothing was scanned here. That is a different thing from the file declaring nothing.`
    case 'read':
      break
  }

  if (file.declarations.length === 0) {
    return 'This file declares nothing its language writes as a type or a routine.'
  }
  // "Has" and not "may hold": the server reads one declaration past what it reports, so a capped
  // list is one it has actually seen more than. It said "may hold" while a second ceiling — a scan
  // that stopped early — could also raise the flag, and the two are one ceiling again.
  return file.capped
    ? `Only the first ${file.declarations.length} are listed; the file has more.`
    : null
}

/**
 * What the panel says about the strength of what it shows. Said the same way the import panels say
 * theirs, because it is the same kind of claim: a declaration is read from the shape of a line in
 * the language the file is written in, and is strong evidence and never proof (CONTEXT.md,
 * _Declaration_).
 */
export const DECLARATION_EVIDENCE =
  'Read from the shape of each line, not from a compiler. A form no profile knows is one this did ' +
  'not find rather than one that is not there. Strong evidence, not proof.'

/**
 * What to call a declaration in the list. The member where there is one, because that is the name a
 * reader is looking for, and the type it belongs to in front of it where the line names both —
 * Delphi's `procedure TCustomer.Save;` says which type the routine is on, and dropping it would list
 * three `Save`s that look like one.
 */
export function declarationLabel(declaration: Declaration): string {
  if (declaration.member === null) return declaration.type ?? ''
  return declaration.type === null
    ? declaration.member
    : `${declaration.type}.${declaration.member}`
}
