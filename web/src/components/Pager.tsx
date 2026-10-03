import { Link } from '@tanstack/react-router'
import { Button } from '@/components/ui/button'
import { formatCount } from '@/lib/format'
import { pageSpan } from '@/lib/paging'

/**
 * Where you are in a paged list and how to leave it. Every paged view in the app is one of these —
 * the file listing, the search results, the change log, a commit's files — and each had written its
 * own, which is how one of them came to count its pages with a thousands separator and the others
 * without.
 *
 * It renders nothing on a single page: a pager that says "Page 1 of 1" is a row of disabled buttons
 * asking to be read.
 *
 * `page` is the page the list answered with, never the one the URL asked for. The file listing
 * answers a page past the end as the last one; search and the change log answer it as itself, with
 * no rows, so from past the end the way back leads to the last page rather than to the one before.
 *
 * The buttons are links to the same view with only the page changed, so every other parameter
 * travels with it without the pager knowing what they are, and a page can be opened in a new tab.
 * They do not preload on hover as the router's other links do: the next page of a search is a whole
 * search, and passing the mouse over the pager is not asking for one.
 */
export function Pager({
  page,
  total,
  pageSize,
  previousLabel = 'Previous',
  nextLabel = 'Next',
  firstPageBare = false,
}: {
  page: number
  total: number
  pageSize: number
  /** What the two directions are called, where a view names them for what it holds — a log is older and newer. */
  previousLabel?: string
  nextLabel?: string
  /** Leaves `page` out of the URL for the first page, for a view whose plain link is that page. */
  firstPageBare?: boolean
}) {
  const { lastPage } = pageSpan(page, pageSize, total)
  if (lastPage <= 1) return null

  function step(target: number, label: string, disabled: boolean) {
    if (disabled) {
      return (
        <Button variant="outline" size="sm" disabled>
          {label}
        </Button>
      )
    }
    return (
      <Button
        variant="outline"
        size="sm"
        nativeButton={false}
        render={
          <Link
            to="."
            preload={false}
            search={(previous: Record<string, unknown>) => ({
              ...previous,
              page: firstPageBare && target === 1 ? undefined : target,
            })}
          />
        }
      >
        {label}
      </Button>
    )
  }

  return (
    <div className="flex items-center gap-4 pt-1">
      <span className="text-sm text-muted-foreground tabular-nums">
        Page {formatCount(page)} of {formatCount(lastPage)}
      </span>
      <div className="ml-auto flex gap-2">
        {step(Math.min(page - 1, lastPage), previousLabel, page <= 1)}
        {step(page + 1, nextLabel, page >= lastPage)}
      </div>
    </div>
  )
}
