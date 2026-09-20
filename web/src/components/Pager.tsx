import { Button } from '@/components/ui/button'
import { formatCount } from '@/lib/format'

/**
 * Where you are in a paged list and how to leave it. Every paged view in the app is one of these —
 * the file listing, the search results, the change log — and each had written its own, which is how
 * one of them came to count its pages with a thousands separator and the other two without.
 *
 * It renders nothing on a single page: a pager that says "Page 1 of 1" is a row of disabled buttons
 * asking to be read.
 *
 * Paging is a navigation and not a state change, so it hands the page number back rather than
 * navigating itself: which route and which other parameters travel with it is the caller's business,
 * and a pager that knew them would have to know every view that pages.
 */
export function Pager({
  page,
  lastPage,
  previousLabel = 'Previous',
  nextLabel = 'Next',
  onPage,
}: {
  page: number
  lastPage: number
  /** What the two directions are called, where a view names them for what it holds — a log is older and newer. */
  previousLabel?: string
  nextLabel?: string
  onPage: (page: number) => void
}) {
  if (lastPage <= 1) return null

  return (
    <div className="flex items-center gap-4 pt-1">
      <span className="text-sm text-muted-foreground tabular-nums">
        Page {formatCount(page)} of {formatCount(lastPage)}
      </span>
      <div className="ml-auto flex gap-2">
        <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
          {previousLabel}
        </Button>
        <Button
          variant="outline"
          size="sm"
          disabled={page >= lastPage}
          onClick={() => onPage(page + 1)}
        >
          {nextLabel}
        </Button>
      </div>
    </div>
  )
}
