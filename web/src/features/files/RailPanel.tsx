import { ErrorPanel } from '@/components/ErrorPanel'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { formatCount } from '@/lib/format'

/**
 * One section of the file rail, in every state it can be in: waiting for its request, failed, or
 * answering. Written once because every panel beside the code is the same small card with the same
 * heading, and each hand-written copy of it was a spacing or type-scale fix that landed on one panel
 * and left the others a few pixels off. Every panel waits for its own request, so the wait and the
 * failure look the same in all of them.
 *
 * An answer from the index is a note on what it has to explain about itself, the list, and the
 * caveat on how it was reached; what a file declares and what it imports are both that shape.
 */
export function RailPanel({
  title,
  pending = false,
  error,
  count,
  capped = false,
  note,
  evidence,
  children,
}: {
  title: string
  pending?: boolean
  error?: unknown
  /** Shown beside the heading where there is a number worth seeing without reading the list. */
  count?: number
  /** Whether `count` stopped at a server ceiling, so it is shown as a floor, `500+`, not a total. */
  capped?: boolean
  /** What the answer has to say about itself, or null when the list speaks for itself. */
  note?: string | null
  /**
   * How the answer was reached — every one of these is read from line shape and is evidence rather
   * than proof (CONTEXT.md). Undefined where nothing was read, so there is no claim to qualify.
   */
  evidence?: string
  children?: React.ReactNode
}) {
  return (
    <Card size="sm">
      <CardHeader>
        <CardTitle className="flex items-baseline justify-between gap-2">
          {title}
          {count === undefined ? null : (
            <span className="text-xs font-normal text-muted-foreground tabular-nums">
              {formatCount(count)}
              {capped ? '+' : ''}
            </span>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent>
        {error ? (
          <ErrorPanel error={error} title={`${title} could not be read`} />
        ) : pending ? (
          <div className="space-y-2">
            <Skeleton className="h-4 w-full" />
            <Skeleton className="h-4 w-2/3" />
          </div>
        ) : (
          <>
            {note ? <p className="mb-3 text-sm text-muted-foreground last:mb-0">{note}</p> : null}
            {children}
            {evidence === undefined ? null : (
              <p className="mt-3 text-xs leading-snug text-muted-foreground/80">{evidence}</p>
            )}
          </>
        )}
      </CardContent>
    </Card>
  )
}
