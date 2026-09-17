import { Info } from 'lucide-react'

/**
 * What a view counted over, above the view.
 *
 * Every window in this system ends at the newest commit the index holds and never at the clock
 * (CONTEXT.md, _Window_) — and that is exactly the thing a reader cannot see and will otherwise
 * assume the other way round: a change log that stops two weeks ago looks like a quiet fortnight
 * rather than like an index nobody has refreshed. History and Churn both have to say it, so they
 * say it in the same words and the same place.
 *
 * Above the card rather than inside it, because it is a caveat about the whole view rather than a
 * heading of it.
 */
export function WindowNote({ children }: { children: React.ReactNode }) {
  return (
    <p className="mb-4 flex items-start gap-2 rounded-lg border bg-muted/40 px-4 py-2.5 text-sm text-muted-foreground">
      <Info aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
      <span>{children}</span>
    </p>
  )
}
