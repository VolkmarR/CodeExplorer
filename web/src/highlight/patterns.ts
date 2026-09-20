import type { HighlightTokenClass, TokenRange } from '@tanstack/highlight'

/**
 * One rule of a tokenizer. `group` names a capture to colour instead of the whole match; like the
 * library's own definitions, that group must be the tail of the match, because the offset is derived
 * by subtracting its length from the match's.
 */
export type Pattern = {
  className: HighlightTokenClass | ((match: RegExpExecArray) => HighlightTokenClass)
  group?: number
  regex: RegExp
  /**
   * Colour whatever of the match is still free, instead of giving the whole match up when any of it
   * is taken. For a rule that claims a span containing other code — an attribute or a preprocessor
   * directive, which run after the strings and comments inside them have been claimed — all or
   * nothing means nothing: `[Obsolete("gone")]` lost its whole span to the four characters of
   * `"gone"` and its name then read as a call. Filling colours the brackets and leaves the string a
   * string, which is what the span means and what an editor shows.
   */
  fill?: boolean
}

/**
 * The library's own languages use `patternTokenizer` from `@tanstack/highlight/internal/patterns`,
 * which its exports map does not expose, so this is the same rule written out: patterns run in
 * order, and the first to claim a span keeps it. Order is therefore the whole design of a language
 * definition — comments and strings come first so that a keyword inside a string is not coloured as
 * one.
 */
export function collect(code: string, patterns: readonly Pattern[]): TokenRange[] {
  const ranges: TokenRange[] = []
  const claimed = new Uint8Array(code.length)
  for (const pattern of patterns) {
    // A fresh RegExp per run: `lastIndex` on a shared global pattern would carry over between files.
    const regex = new RegExp(pattern.regex.source, pattern.regex.flags)
    let match: RegExpExecArray | null
    while ((match = regex.exec(code)) !== null) {
      const value = pattern.group === undefined ? match[0] : match[pattern.group]
      if (!value) {
        if (match[0].length === 0) regex.lastIndex++
        continue
      }
      const start = match.index + match[0].length - value.length
      const end = start + value.length
      const className =
        typeof pattern.className === 'function' ? pattern.className(match) : pattern.className
      if (pattern.fill) {
        for (const [from, to] of freeRuns(claimed, start, end)) {
          claimed.fill(1, from, to)
          ranges.push({ className, end: to, start: from })
        }
      } else if (claim(claimed, start, end)) {
        ranges.push({ className, end, start })
      }
    }
  }
  return ranges
}

/**
 * The maximal unclaimed stretches of `[start, end)`, in order. Empty when the span is fully taken,
 * which is how a rule that fills stays out of the way of one that already ran.
 */
function freeRuns(claimed: Uint8Array, start: number, end: number): [number, number][] {
  const runs: [number, number][] = []
  let from = -1
  for (let index = start; index < end; index++) {
    if (claimed[index]) {
      if (from !== -1) runs.push([from, index])
      from = -1
    } else if (from === -1) {
      from = index
    }
  }
  if (from !== -1) runs.push([from, end])
  return runs
}

function claim(claimed: Uint8Array, start: number, end: number): boolean {
  for (let index = start; index < end; index++) {
    if (claimed[index]) return false
  }
  claimed.fill(1, start, end)
  return true
}
