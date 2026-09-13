import type { RefreshStatus } from '@/lib/api'
import { ErrorPanel } from '@/components/ErrorPanel'
import { RefreshSummary } from '@/features/refresh/RefreshSummary'

/**
 * What the last refresh is doing, or did. A queued or running refresh shows the phase the server
 * reports; a finished one shows what it produced or why it stopped. Nothing is shown for a project
 * that has not refreshed on this replica, because "no refresh has run" is not news on a page that
 * already says when the index was built.
 */
export function RefreshProgress({ status }: { status: RefreshStatus }) {
  if (status.state === 'NeverRun') return null

  if (status.state === 'Queued' || status.state === 'Running') {
    return (
      <div className="rounded-lg border bg-card px-4 py-3 text-sm">
        <p className="flex items-center gap-2">
          {/* The dot is the only motion on the page: a rebuild has no percentage to show, because
              the work is a tree walk whose size is not known until it is done. Gated on motion-safe,
              so someone who asked for less motion gets a plain marker and still sees the phase. */}
          <span
            className="size-2 rounded-full bg-primary motion-safe:animate-pulse"
            aria-hidden="true"
          />
          {status.phase}
        </p>
        <p className="mt-1 text-muted-foreground">
          The old index keeps answering searches until the new one is swapped in.
        </p>
      </div>
    )
  }

  // The same panel a failed request gets, so the two errors a project page can show look alike. The
  // server's prose is the message; the title says which of the two this is.
  if (status.state === 'Failed')
    return <ErrorPanel error={status.error} title="The refresh did not finish" />

  return status.summary ? <RefreshSummary summary={status.summary} /> : null
}
