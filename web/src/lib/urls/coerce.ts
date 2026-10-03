/**
 * How a search param is read, written once for every `validate*Search` beside it. Each takes what the
 * router hands over — a string from a pasted or hand-edited URL, the value itself from a typed link —
 * and answers with the type or with absence, never a throw: a broken link still opens a page.
 *
 * Four functions rather than a schema library, which ADR-0004 leaves out: these four readings are all
 * the URLs here need, and a validator built from them stays a short object literal.
 */

/** A page, a line, a window in days: a whole number above zero, or not there. */
export function positiveInteger(value: unknown): number | undefined {
  const number = Number(value)
  return Number.isInteger(number) && number > 0 ? number : undefined
}

/**
 * A string, or not there. The empty string is not there, like everywhere: `repository=` is the
 * select's way of saying every repository, not a repository with no name.
 */
export function text(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}

/** A string, with the empty string as its resting state, for a field that is always present. */
export function textOrEmpty(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

/** True from a typed link or the word `true` in a URL; anything else, `false` included, is off. */
export function flag(value: unknown): boolean {
  return value === true || value === 'true'
}
