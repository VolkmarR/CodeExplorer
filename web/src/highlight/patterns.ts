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
      if (claim(claimed, start, end)) {
        ranges.push({
          className:
            typeof pattern.className === 'function' ? pattern.className(match) : pattern.className,
          end,
          start,
        })
      }
    }
  }
  return ranges
}

function claim(claimed: Uint8Array, start: number, end: number): boolean {
  for (let index = start; index < end; index++) {
    if (claimed[index]) return false
  }
  claimed.fill(1, start, end)
  return true
}
