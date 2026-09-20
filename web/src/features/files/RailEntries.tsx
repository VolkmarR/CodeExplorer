import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { formatCount } from '@/lib/format'

/**
 * How many entries a rail panel shows before the reader asks for the rest. A rail is read alongside
 * the code, and one panel with two hundred entries would push every panel under it out of the rail —
 * while a file with a handful should need no second click to see them all.
 *
 * Not exported: every panel reaches it through this component, which is the point of it being one.
 */
const ENTRIES_SHOWN = 8

/**
 * A panel's list and the way out of a long one: a screenful, and a button saying how many are being
 * held back. Its own component because every list in the rail is one — what a file declares, what it
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
  const shown = expanded ? items : items.slice(0, ENTRIES_SHOWN)

  if (items.length === 0) return null

  return (
    <>
      <ul className="space-y-2.5">{shown.map(children)}</ul>
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
