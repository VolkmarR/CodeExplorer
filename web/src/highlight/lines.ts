import type { HighlightRenderNode } from '@tanstack/highlight'
import { renderTokens } from '@tanstack/highlight'
import { highlighter } from '@/highlight/highlighter'

/**
 * A file, split into the lines the code view draws a row each of, and coloured only where it is
 * read. The view is virtualised, so it asks for a few dozen lines of a file that may have a hundred
 * thousand, and tokenizing all of them to answer would be the whole cost the virtualiser exists to
 * avoid.
 */
export interface CodeLines {
  count: number
  /** The coloured spans of one line, by zero-based index. */
  nodesAt(index: number): readonly HighlightRenderNode[]
}

/**
 * Lines per tokenized block. Colouring is done a block at a time rather than a line at a time
 * because a pattern run costs more per call than per character: measured on a 5.1 MiB, 100k-line C#
 * file (Node 24, this machine), a 250-line block costs well under a millisecond, so the blocks a
 * screenful spans are far below a frame, while the whole file costs 188 ms to tokenize and 165 ms
 * more to render into nodes — a third of the second the whole page has.
 */
const BLOCK_LINES = 250

/**
 * Below this, the file is tokenized in one pass. A block boundary cuts a construct that spans lines
 * — a block comment, a verbatim string — so the tail of one is coloured as code until the block
 * holding its opening is asked for, which it never is if the reader never scrolls there. Nearly
 * every file in an index is smaller than this and pays nothing for the accuracy; the few that are
 * not are the ones that cannot afford the whole pass.
 */
const WHOLE_FILE_BYTES = 256 * 1024

/**
 * The last file asked for, kept so that a re-render does not re-colour it. The file view shows one
 * file at a time, and the things that re-render it — the wrap toggle, the blame runs arriving, a
 * scroll that brings new rows in — all pass the same content back. A module-level entry rather than
 * a `useMemo`, because the blocks already coloured have to survive the re-render too, and a cache
 * keyed on identity says that plainly where a memo would only imply it.
 */
let cached: { content: string; language: string; lines: CodeLines } | undefined

export function codeLines(content: string, language: string): CodeLines {
  if (cached && cached.content === content && cached.language === language) return cached.lines

  const lines = content.split('\n')
  const source: CodeLines =
    content.length <= WHOLE_FILE_BYTES
      ? wholeFile(content, language, lines.length)
      : byBlock(lines, language)

  cached = { content, language, lines: source }
  return source
}

function wholeFile(content: string, language: string, count: number): CodeLines {
  const nodes = colour(content, language, count)
  return { count, nodesAt: (index) => nodes[index] ?? [] }
}

function byBlock(lines: string[], language: string): CodeLines {
  const blocks = new Map<number, HighlightRenderNode[][]>()

  return {
    count: lines.length,
    nodesAt(index) {
      const block = Math.floor(index / BLOCK_LINES)
      let coloured = blocks.get(block)
      if (!coloured) {
        const start = block * BLOCK_LINES
        const text = lines.slice(start, start + BLOCK_LINES).join('\n')
        coloured = colour(text, language, Math.min(BLOCK_LINES, lines.length - start))
        blocks.set(block, coloured)
      }
      return coloured[index - block * BLOCK_LINES] ?? []
    },
  }
}

/**
 * One stretch of text, tokenized and rendered into a row of nodes per line.
 *
 * The array is filled by the line number the renderer stamps rather than by the order the nodes come
 * in: `lineNumbers` is what makes the renderer wrap each line in a node of its own, it puts a text
 * node holding the newline between them, and a line that is empty gets no node at all. Reading the
 * number back is what keeps an empty line from shifting every line under it.
 */
function colour(text: string, language: string, count: number): HighlightRenderNode[][] {
  const rows: HighlightRenderNode[][] = Array.from({ length: count }, () => [])
  const { tokens } = highlighter.tokenize(text, { lang: language })
  for (const node of renderTokens(tokens, { lineNumbers: true })) {
    if (node.type !== 'element' || !node.classNames.includes('th-line')) continue
    const number = Number(node.data?.line)
    if (number >= 1 && number <= count) rows[number - 1] = node.children
  }
  return rows
}
