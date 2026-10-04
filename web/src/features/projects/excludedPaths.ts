import { formatCountOf } from '@/lib/format'

/**
 * The excluded-paths note up to its link (#216): "6,712 files at HEAD are left out by the", which
 * the note finishes with "excluded paths." `which` says which files were counted, after the noun.
 * `from` names the section, for a page where two cards count the same files and the same note
 * under both would read as one sentence printed twice.
 */
export function excludedLead(files: number, which: string, from?: string): string {
  const verb = files === 1 ? 'is' : 'are'
  const section = from ? ` of ${from}` : ''
  return `${formatCountOf(files, 'file')} ${which} ${verb} left out${section} by the`
}

/**
 * The draft with `pattern` added as its last line (#217), unless a line already holds it — compared
 * ignoring case, as the server compares when it drops repeats. Trailing blank lines are dropped
 * first so that suggestions added one after another stay one per line.
 */
export function withPattern(draft: string, pattern: string): string {
  const lines = draft.split('\n').map((line) => line.trim())
  if (lines.some((line) => line.toLowerCase() === pattern.toLowerCase())) return draft
  const kept = draft.replace(/\s+$/, '')
  return kept === '' ? pattern : `${kept}\n${pattern}`
}
