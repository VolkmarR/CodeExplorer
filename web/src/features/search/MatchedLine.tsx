import type { HighlightRenderNode } from '@tanstack/highlight'
import { renderTokens } from '@tanstack/highlight'
import type { MatchRange } from '@/features/search/matchRanges'
import { highlighter } from '@/highlight/highlighter'

/** One leaf of a highlighted line: a run of text and the token classes above it, joined. */
interface Leaf {
  text: string
  className: string
}

/** A leaf, or the part of one, that is either wholly inside a match or wholly outside every match. */
interface Piece extends Leaf {
  matched: boolean
}

/**
 * One line of a search result, syntax-coloured like the file view and with the query's matches
 * marked. The two are separate layers over the same text — a token can hold half a match, a match can
 * span three tokens — so the highlighter's tree is flattened to leaves and each leaf is cut where a
 * match begins or ends. That is why this does not reuse CodeView's renderer, which keeps the tree.
 */
export function MatchedLine({
  text,
  language,
  ranges,
}: {
  text: string
  language: string
  ranges: MatchRange[]
}) {
  const pieces = cut(leavesOf(text, language), ranges)
  return pieces.map((piece, index) => {
    const span = piece.className ? (
      <span className={piece.className}>{piece.text}</span>
    ) : (
      piece.text
    )
    return (
      // Position is the identity here for the same reason as in CodeView's renderer: the pieces of a
      // line are never reordered, only replaced all at once.
      // oxlint-disable-next-line react/no-array-index-key, react-doctor/no-array-index-key, react-doctor/no-array-index-as-key
      <span key={index}>
        {piece.matched ? (
          <mark className="rounded-xs bg-amber-300/35 text-inherit">{span}</mark>
        ) : (
          span
        )}
      </span>
    )
  })
}

/** The highlighter's tree for one line, flattened to text runs with their classes. */
function leavesOf(text: string, language: string): Leaf[] {
  const { tokens } = highlighter.tokenize(text, { lang: language })
  const leaves: Leaf[] = []
  for (const node of renderTokens(tokens)) collectLeaves(node, '', leaves)
  return leaves
}

function collectLeaves(node: HighlightRenderNode, className: string, into: Leaf[]) {
  if (node.type === 'text') {
    into.push({ className, text: node.value })
    return
  }
  const joined = [className, ...node.classNames].filter(Boolean).join(' ')
  for (const child of node.children) collectLeaves(child, joined, into)
}

/**
 * Cuts the leaves at every match boundary. Both lists are in text order, so one pass with a cursor
 * into each is enough: a leaf is emitted whole when no range touches it, otherwise in the parts before,
 * inside and after each range that does.
 */
function cut(leaves: Leaf[], ranges: MatchRange[]): Piece[] {
  const pieces: Piece[] = []
  let offset = 0
  let range = 0
  for (const leaf of leaves) {
    let from = 0
    while (from < leaf.text.length) {
      while (range < ranges.length && ranges[range].end <= offset + from) range++
      const current = ranges[range]
      const matched = current !== undefined && current.start <= offset + from
      const boundary =
        current === undefined ? leaf.text.length : (matched ? current.end : current.start) - offset
      const to = Math.min(leaf.text.length, boundary)
      pieces.push({ className: leaf.className, matched, text: leaf.text.slice(from, to) })
      from = to
    }
    offset += leaf.text.length
  }
  return pieces
}
