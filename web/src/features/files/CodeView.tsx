import type { HighlightRenderNode } from '@tanstack/highlight'
import { Link } from '@tanstack/react-router'
import { useVirtualizer } from '@tanstack/react-virtual'
import { useEffect, useRef, type CSSProperties } from 'react'
import { languageFor } from '@/highlight/highlighter'
import { codeLines } from '@/highlight/lines'
import type { Origin } from '@/lib/urls/views'
import { fileSearch } from '@/lib/urls/fileParams'
import { commitSearch } from '@/lib/urls/commitParams'
import type { BlameRun } from '@/features/files/api'
import { ScrollArea } from '@/components/ui/scroll-area'
import { formatDate, shortSha } from '@/lib/format'
import { cn } from '@/lib/utils'

/**
 * What one unwrapped row is tall, in pixels: `text-xs` at the leading Tailwind gives it, plus the
 * row's own padding. It is the virtualiser's first guess and not a rule — every rendered row is
 * measured, so a row that is taller because the line wrapped, or because the font did not load the
 * way this assumed, still sits where it is drawn. A guess close to the truth is what keeps the
 * scrollbar honest over the lines nobody has looked at yet.
 */
const ROW_HEIGHT = 20

/**
 * A file with line numbers, highlighted by `@tanstack/highlight` — including the C# definition this
 * repo owns, which is why the file view works at all on the language most of these projects are in.
 *
 * The library's nodes are rendered as React elements rather than through its HTML string, so no
 * markup has to be trusted into `dangerouslySetInnerHTML` and every line can carry an anchor the URL
 * points at.
 *
 * Only the rows on screen exist. The index accepts files up to 4 MiB, which is a hundred thousand
 * rows of three cells each if they were all drawn, so the pane is row-virtualised with
 * `@tanstack/react-virtual` (ADR-0004) and the colouring is done per block of lines under it
 * (`highlight/lines.ts`). That is also why the rows are divs and no longer a `<table>`: a virtualiser
 * positions its rows absolutely, and absolutely positioned `<tr>`s are not rows any more — the
 * table's own layout is the thing being replaced.
 *
 * With `blame` the row gains a gutter to the left of the numbers: one label per run of lines that
 * share a commit, on the run's first line, and a tint that alternates run by run so the eye can see
 * where one ends. It is a gutter and not a hover, because the question it answers — who wrote this
 * stretch — is asked of a region and not of a line.
 */
export function CodeView({
  project,
  content,
  path,
  line,
  wrap = false,
  blame,
  origin,
}: {
  project: string
  content: string
  path: string
  line?: number
  wrap?: boolean
  /** How this page was reached, so clicking a line number keeps the trail the reader arrived on. */
  origin?: Origin
  /**
   * Undefined when the gutter is off, null while the runs are on their way, and the runs once they
   * have arrived — which may be none, for a file the history could not attribute at all.
   */
  blame?: BlameRun[] | null
}) {
  const lines = codeLines(content, languageFor(path))
  const runs = blameByLine(blame)
  const gutter = blame !== undefined

  const viewport = useRef<HTMLDivElement>(null)
  // React Compiler will not optimize a component that calls this hook, because the virtualiser hands
  // back functions it re-creates as it measures and a memoized copy of one would report the sizes of
  // a scroll position that has passed. Accepted here rather than worked around: what re-rendering
  // this component costs is one screenful of rows, the colouring is cached outside React in
  // `highlight/lines.ts`, and none of the virtualiser's functions are handed to anything memoized.
  // oxlint-disable-next-line react/incompatible-library
  const rows = useVirtualizer({
    count: lines.count,
    estimateSize: () => ROW_HEIGHT,
    getScrollElement: () => viewport.current,
    // Enough rows above and below to cover a wheel flick and a page key before the next render, and
    // few enough that a screenful is still a screenful of work.
    overscan: 24,
  })

  // Wrapping changes what every row is tall, and a measured height is remembered per row, so the
  // measurements taken in the other mode have to be dropped rather than corrected one scroll at a
  // time.
  useEffect(() => {
    rows.measure()
  }, [wrap, rows])

  // The line the URL names, brought into the middle of the pane. This was a ref callback on the row
  // while every row existed; with only a screenful of them mounted the row is usually not one of
  // them, so the request goes to the virtualiser instead, which scrolls to an index whether or not
  // it is drawn. `path` is a dependency as well as `line`: opening a different file at the same line
  // number is a second request to scroll, and the rows under it are new.
  useEffect(() => {
    if (line !== undefined && line >= 1 && line <= lines.count)
      rows.scrollToIndex(line - 1, { align: 'center' })
  }, [line, path, lines.count, rows])

  return (
    // The pane scrolls rather than the page, so the rail beside it and the card's own header stay
    // where they are while a long file is read. The height is what is left under the top bar and
    // the card head; a pane shorter than that still only takes the room it needs, because this is a
    // bound and not a size.
    //
    // The viewport element is handed out because it is what scrolls, and the virtualiser measures
    // and drives the thing that scrolls rather than the box around it.
    //
    // The frame is a div of its own because ScrollArea owns its appearance (`shadcn/no-restyle`);
    // overflow-hidden clips the scrollbar to the frame's rounded corners.
    <div className="overflow-hidden rounded-lg border bg-card">
      <ScrollArea className="max-h-(--reading-pane)" viewportRef={viewport}>
        <div
          className="relative h-(--rows-height) w-full font-mono text-xs"
          style={{ '--rows-height': `${rows.getTotalSize()}px` } as CSSProperties}
        >
          {rows.getVirtualItems().map((row) => {
            const number = row.index + 1
            const attributed = runs.get(number)
            return (
              <div
                key={row.key}
                id={`L${number}`}
                data-index={row.index}
                // Measured rather than assumed: a wrapped line is as tall as it needs to be, and the
                // gutter's label can be taller than the code beside it.
                ref={rows.measureElement}
                className={cn(
                  'absolute top-0 left-0 flex translate-y-(--row-start)',
                  // Unwrapped, a row is as wide as its longest line and the pane scrolls sideways to
                  // it; wrapped, it is the pane's width and the code folds inside it.
                  wrap ? 'w-full' : 'min-w-full',
                  number === line && 'bg-primary/15',
                )}
                style={{ '--row-start': `${row.start}px` } as CSSProperties}
              >
                {gutter ? (
                  <div
                    className={cn(
                      'w-56 shrink-0 border-r px-2 py-0.5 whitespace-nowrap select-none',
                      // Alternating, so two adjacent runs by the same author read as two.
                      attributed && attributed.index % 2 === 1 && 'bg-muted/40',
                    )}
                    title={attributed?.run.by?.subject}
                  >
                    {blame === null ? (
                      number === 1 ? (
                        <span className="text-muted-foreground">loading…</span>
                      ) : null
                    ) : attributed?.first ? (
                      attributed.run.by ? (
                        <span className="flex items-baseline gap-2 overflow-hidden">
                          {/* The abbreviated id is the link and not the whole line: the column is
                            narrow and already holds three things, and the sha is the one of them
                            that names the commit. */}
                          <Link
                            to="/projects/$project/commit"
                            params={{ project }}
                            search={commitSearch(attributed.run.by.sha, origin?.view ?? 'files')}
                            className="text-muted-foreground hover:text-primary hover:underline"
                          >
                            {shortSha(attributed.run.by.sha)}
                          </Link>
                          <span className="text-muted-foreground">
                            {formatDate(attributed.run.by.authoredAt)}
                          </span>
                          <span className="truncate">{attributed.run.by.authorName}</span>
                        </span>
                      ) : (
                        // A run no commit of the walk wrote: history the index does not reach, not
                        // a line nobody wrote. Said as such, quietly, rather than left blank.
                        <span className="text-muted-foreground/70">not attributed</span>
                      )
                    ) : null}
                  </div>
                ) : null}
                <div
                  className={cn(
                    'w-14 shrink-0 border-r px-2 py-0.5 text-right text-muted-foreground/70 select-none',
                    number === line && 'shadow-gutter-mark text-primary',
                  )}
                >
                  {/* The number is the link to itself, so the way to share a line from the file is the
                    same as from a search result: click the number, copy the address bar. `replace`,
                    because each line clicked is not a page the back button should revisit. */}
                  <Link
                    to="/projects/$project/file"
                    params={{ project }}
                    search={fileSearch(path, number, origin)}
                    replace
                    className="hover:text-primary hover:underline"
                  >
                    {number}
                  </Link>
                </div>
                <div
                  className={cn(
                    'px-3 py-0.5',
                    wrap ? 'min-w-0 flex-1 break-all whitespace-pre-wrap' : 'whitespace-pre',
                  )}
                >
                  {renderNodes(lines.nodesAt(row.index))}
                </div>
              </div>
            )
          })}
        </div>
      </ScrollArea>
    </div>
  )
}

/**
 * The run each line is in, looked up by line number, with whether it is the run's first line. A map
 * and not a search per row: the rows come and go as the pane scrolls, and a scan per row would be
 * paid again each time one comes back.
 */
function blameByLine(blame: BlameRun[] | null | undefined) {
  const byLine = new Map<number, { run: BlameRun; first: boolean; index: number }>()
  blame?.forEach((run, index) => {
    for (let number = run.startLine; number <= run.endLine; number++)
      byLine.set(number, { first: number === run.startLine, index, run })
  })
  return byLine
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
