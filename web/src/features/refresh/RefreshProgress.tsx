import type { RefreshProgress as Step, RefreshStatus } from '@/features/refresh/api'
import { ErrorPanel } from '@/components/ErrorPanel'
import { RefreshSummary } from '@/features/refresh/RefreshSummary'
import { isRefreshRunning } from '@/features/refresh/queries'
import { Progress, ProgressValue } from '@/components/ui/progress'
import { formatCount } from '@/lib/format'

/**
 * What the last refresh is doing, or did. A queued or running refresh shows which step it is on and,
 * where the step can count its work, how far through it is; a finished one shows what it produced or
 * why it stopped. Nothing is shown for a project that has not refreshed on this replica, because "no
 * refresh has run" is not news on a page that already says when the index was built.
 */
export function RefreshProgress({ status }: { status: RefreshStatus }) {
  if (status.state === 'NeverRun') return null

  // The per-phase breakdown #92 added beside this panel is gone: a running refresh already names the
  // step it is on and counts its work, and a finished one is read for what it produced, not for how
  // its seconds divided. `phases` stays on the status, where `/api/projects/{slug}/refresh` and the
  // telemetry still carry it for anyone measuring a refresh rather than watching one.
  return <Panel status={status} />
}

/** What the refresh is doing or did. */
function Panel({ status }: { status: RefreshStatus }) {
  if (isRefreshRunning(status)) {
    const progress = status.progress
    return (
      <div className="rounded-lg border bg-card px-4 py-3 text-sm">
        <p className="flex items-center gap-2">
          {/* The dot is the motion while a step has nothing to count. Gated on motion-safe, so
              someone who asked for less motion gets a plain marker and still sees the phase. */}
          <span
            className="size-2 shrink-0 rounded-full bg-primary motion-safe:animate-pulse"
            aria-hidden="true"
          />
          {progress ? (
            <span className="text-muted-foreground tabular-nums">
              Step {progress.step} of {progress.totalSteps}
            </span>
          ) : null}
          <span>{progress?.phase ?? status.phase}</span>
        </p>
        {progress ? <StepProgress progress={progress} /> : null}
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

/**
 * How far through its own work the current step is. A bar only when the server sent a total, and a
 * count alone when it sent only what is done so far — the commit walk knows how far it has got and
 * not how far it is going. Deliberately per step and never overall: the steps are wildly unequal,
 * and one bar weighted as though they were not would race to most of the way and then sit still.
 */
function StepProgress({ progress }: { progress: Step }) {
  if (progress.done === null) return null
  if (progress.total === null || progress.total === 0)
    return (
      <p className="mt-1 text-muted-foreground tabular-nums">{formatCount(progress.done)} so far</p>
    )

  return (
    // Was a native `<progress>` styled through three engines' pseudo-elements. The shadcn one
    // (ADR-0004) reads the same to assistive technology, draws the same in every engine, and puts
    // the percentage in `ProgressValue` rather than in a number this component computes.
    <Progress value={progress.done} max={progress.total} className="mt-2 items-center">
      <ProgressValue className="order-last shrink-0 text-xs">
        {(formatted) => (
          <>
            {formatCount(progress.done ?? 0)} / {formatCount(progress.total ?? 0)} · {formatted}
          </>
        )}
      </ProgressValue>
    </Progress>
  )
}
