import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { formatCount } from '@/lib/format'

/**
 * How many entries a rail panel shows before the reader asks for the rest. A rail is read alongside
 * the code, and one panel with two hundred entries would push every panel under it out of the rail —
 * while a file with a handful should need no second click to see them all.
 */
export const ENTRIES_SHOWN = 8

/**
 * One section of the file rail. Its own component because every panel beside the code is the same
 * small card with the same heading, and a second hand-written copy of the shell is the thing that
 * makes one panel sit a few pixels off from the ones above it.
 */
export function RailPanel({
  title,
  count,
  children,
}: {
  title: string
  /**
   * Shown beside the heading where there is a number worth seeing without reading the list. Already
   * written out, because a count that stopped at a ceiling is not a plain number — `500+`.
   */
  count?: string
  children: React.ReactNode
}) {
  return (
    <Card size="sm">
      <CardHeader>
        <CardTitle className="flex items-baseline justify-between gap-2">
          {title}
          {count === undefined ? null : (
            <span className="text-xs font-normal text-muted-foreground tabular-nums">{count}</span>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent>{children}</CardContent>
    </Card>
  )
}

/**
 * A panel's list and the way out of a long one: a screenful, and a button saying how many are being
 * held back. Beside `RailPanel` because every list in the rail is one — what a file declares, what it
 * imports, what imports it — and they differ in their rows and in nothing else, so a fix to the
 * wrapper made in one of them is a fix the others would not get.
 *
 * An opened list grows to its full length and the rail it sits in is what scrolls. It had a scroll
 * of its own, which was right while the rail scrolled with the page and is a second scrollbar inside
 * the first now that the rail holds one.
 *
 * It owns the expansion rather than taking it as a prop, because nothing outside it reads that state
 * and a panel that held it would hold it twice.
 */
export function RailEntries<T>({
  items,
  capped,
  children,
}: {
  items: T[]
  /** Whether the length is a server ceiling rather than the count: the button must not say "all". */
  capped: boolean
  children: (item: T) => React.ReactNode
}) {
  const [expanded, setExpanded] = useState(false)

  if (items.length === 0) return null

  return (
    <>
      <ul className="space-y-2.5">
        {(expanded ? items : items.slice(0, ENTRIES_SHOWN)).map(children)}
      </ul>
      {items.length > ENTRIES_SHOWN ? (
        <Button
          variant="ghost"
          size="sm"
          className="mt-2 -ml-2"
          onClick={() => setExpanded((open) => !open)}
        >
          {expanded
            ? 'Show fewer'
            : `Show ${capped ? 'the first' : 'all'} ${formatCount(items.length)}`}
        </Button>
      ) : null}
    </>
  )
}

/**
 * A panel's content while its answer is on the way. Beside `RailPanel` and not in one of the panels
 * because every panel in the rail waits for its own request, and a placeholder that differs between
 * them reads as two different kinds of waiting.
 */
export function RailPending() {
  return (
    <div className="space-y-2">
      <Skeleton className="h-4 w-full" />
      <Skeleton className="h-4 w-2/3" />
    </div>
  )
}
