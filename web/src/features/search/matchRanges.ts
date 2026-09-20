import type { SearchParameters } from '@/lib/urls/searchParams'

/** A half-open span `[start, end)` of one line's text that the query matched. */
export interface MatchRange {
  start: number
  end: number
}

/**
 * The pattern to mark matches with on the client, built from what was sent to the server. The server
 * answers with whole lines and no offsets, so where in the line the query hit is worked out again
 * here — with JavaScript's regex engine standing in for RE2. The two agree on everything a search
 * here can express (RE2 has no lookaround or backreferences to disagree about), and a pattern only
 * one of them accepts marks nothing rather than throwing: the server has already said what matched.
 *
 * Text mode matches whole identifiers on the server, so the marker asks for word boundaries too; a
 * query that is not itself a word (`->`, `.Result`) gets a plain substring mark instead, since `\b`
 * beside a symbol matches nowhere.
 */
export function matchPattern(search: SearchParameters): RegExp | null {
  const flags = search.caseSensitive ? 'g' : 'gi'
  try {
    // In regex mode the query is a pattern by definition; escaping it is the text mode below.
    // oxlint-disable-next-line react-doctor/no-unescaped-dynamic-string-in-regexp
    if (search.regex) return new RegExp(search.q, flags)
    const escaped = search.q.replaceAll(/[$()*+.?[\\\]^{|}]/g, String.raw`\$&`)
    const word = /^\w.*\w$|^\w$/.test(search.q)
    return new RegExp(word ? String.raw`\b${escaped}\b` : escaped, flags)
  } catch {
    // A pattern this engine rejects. The lines still show; only the marks are missing.
    return null
  }
}

/** Every match of the pattern in one line, in order and non-overlapping. */
export function matchRanges(text: string, pattern: RegExp | null): MatchRange[] {
  if (!pattern) return []
  const ranges: MatchRange[] = []
  pattern.lastIndex = 0
  for (const match of text.matchAll(pattern)) {
    // An empty match (`a*` on a line without one) would mark nothing and loop forever if advanced by
    // hand; `matchAll` advances past it itself, and there is nothing to mark.
    if (match[0].length > 0) ranges.push({ end: match.index + match[0].length, start: match.index })
  }
  return ranges
}
