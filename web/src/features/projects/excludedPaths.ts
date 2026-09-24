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
