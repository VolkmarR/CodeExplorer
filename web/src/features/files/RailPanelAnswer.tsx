import { RailPanel } from '@/features/files/RailPanel'

/**
 * A panel that answers from the index: the heading and its count, whatever the answer has to explain
 * about itself, the list, and the caveat on how the answer was reached.
 *
 * All three such panels — what a file declares, what it imports, what imports it — are this shape and
 * differ only in their rows, so it is written once. Each was hand-written before, and the cost was
 * four literal class strings per copy: a spacing or type-scale fix landed on one panel and left the
 * others a few pixels off, which is the drift `RailPanel` itself exists to prevent.
 */
export function RailPanelAnswer({
  title,
  count,
  note,
  evidence,
  children,
}: {
  title: string
  count?: string
  /** What the answer has to say about itself, or null when the list speaks for itself. */
  note: string | null
  /**
   * How the answer was reached — every one of these is read from line shape and is evidence rather
   * than proof (CONTEXT.md). Undefined where nothing was read, so there is no claim to qualify.
   */
  evidence?: string
  children: React.ReactNode
}) {
  return (
    <RailPanel title={title} count={count}>
      {note ? <p className="mb-3 text-sm text-muted-foreground last:mb-0">{note}</p> : null}
      {children}
      {evidence === undefined ? null : (
        <p className="mt-3 text-xs leading-snug text-muted-foreground/80">{evidence}</p>
      )}
    </RailPanel>
  )
}
