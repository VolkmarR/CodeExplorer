import type { HighlightRenderNode } from '@tanstack/highlight'
import { renderTokens } from '@tanstack/highlight'
import { useCallback, useMemo } from 'react'
import { highlighter, languageFor } from '@/highlight/highlighter'
import { cn } from '@/lib/utils'

/**
 * A file with line numbers, highlighted by `@tanstack/highlight` — including the C# definition this
 * repo owns, which is why the file view works at all on the language most of these projects are in.
 *
 * The library's nodes are rendered as React elements rather than through its HTML string, so no
 * markup has to be trusted into `dangerouslySetInnerHTML` and every line can carry an anchor the URL
 * points at.
 */
export function CodeView({
  content,
  path,
  line,
}: {
  content: string
  path: string
  line?: number
}) {
  const lines = useMemo(() => {
    const { tokens } = highlighter.tokenize(content, { lang: languageFor(path) })
    // `lineNumbers` is what makes the renderer wrap each line in its own node, and the renderer also
    // stamps each with its number; the numbers are read back from there rather than counted here, so
    // the row's key is the line's own identity and not its position in an array.
    const numbered: { children: HighlightRenderNode[]; number: number }[] = []
    for (const node of renderTokens(tokens, { lineNumbers: true })) {
      // The renderer puts a text node holding the newline between lines; only the elements are lines.
      if (isLineElement(node))
        numbered.push({ children: node.children, number: Number(node.data?.line) })
    }
    return numbered
  }, [content, path])

  // A ref callback rather than an effect: it fires exactly when the row the URL names is mounted,
  // which is also when a different file has just replaced the rows.
  const reveal = useCallback((row: HTMLTableRowElement | null) => {
    row?.scrollIntoView({ block: 'center' })
  }, [])

  return (
    <div className="overflow-x-auto rounded-lg border bg-card">
      <table className="w-full border-collapse font-mono text-xs">
        <tbody>
          {lines.map(({ children, number }) => (
            <tr
              key={number}
              id={`L${number}`}
              ref={number === line ? reveal : undefined}
              className={cn(number === line && 'bg-primary/15')}
            >
              <td className="w-14 border-r px-2 py-0.5 text-right align-top text-muted-foreground select-none">
                {number}
              </td>
              <td className="px-3 py-0.5 whitespace-pre">{renderNodes(children)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function isLineElement(
  node: HighlightRenderNode,
): node is Extract<HighlightRenderNode, { type: 'element' }> {
  return node.type === 'element' && node.classNames.includes('th-line')
}

/**
 * The spans of one line, keyed by position. Nothing is inserted, removed or reordered within a line
 * and a span holds no state — a re-tokenize replaces the whole line at once — so there is no
 * data-dependent key to use instead, and the index is the honest one.
 */
function renderNodes(nodes: readonly HighlightRenderNode[]): React.ReactNode {
  return nodes.map((node, index) =>
    node.type === 'text' ? (
      node.value
    ) : (
      // oxlint-disable-next-line react/no-array-index-key, react-doctor/no-array-index-key, react-doctor/no-array-index-as-key
      <span key={index} className={node.classNames.join(' ')}>
        {renderNodes(node.children)}
      </span>
    ),
  )
}
