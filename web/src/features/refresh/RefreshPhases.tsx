import type { PhaseCost } from '@/features/refresh/api'

/**
 * What a refresh spent, phase by phase. The status holds one phase at a time, so until #92 a refresh
 * that had finished could not say where its time went and an operator had to poll fast enough to
 * catch phases lasting milliseconds. The largest bar is usually the full-text index — 27.9 s of a
 * Radix refresh, and the cost this project has decided to accept and to show rather than hide.
 *
 * Ordered as the refresh ran rather than by size: the question is which phase the wall clock went
 * into, and a reader comparing two refreshes compares the same rows in the same places.
 */
export function RefreshPhases({
  phases,
  wallSeconds,
}: {
  phases: PhaseCost[]
  /** The whole refresh, where it has ended. Absent while it runs, when there is no whole yet. */
  wallSeconds: number | null
}) {
  if (phases.length === 0) return null

  const total = phases.reduce((sum, cost) => sum + cost.seconds, 0)
  // Against the longest phase and not against the total, so the bars use the full width whatever the
  // shape of the refresh; the seconds beside each one are what says how much of the total it was.
  const longest = Math.max(...phases.map((cost) => cost.seconds))

  return (
    <div className="mt-2 rounded-lg border bg-card px-4 py-3 text-sm">
      <p className="text-muted-foreground">
        <Attribution phases={phases} total={total} wallSeconds={wallSeconds} />
      </p>
      <ul className="mt-2 space-y-1">
        {keyed(phases).map(({ cost, key }) => (
          <li key={key} className="flex items-center gap-3">
            <span className="w-14 shrink-0 text-right text-muted-foreground tabular-nums">
              {formatSeconds(cost.seconds)}
            </span>
            {/* Decorative: the seconds beside it carry the same value to a screen reader, and a bar
                that announced itself would read every phase twice. */}
            <span className="h-1.5 w-24 shrink-0 rounded-full bg-muted" aria-hidden="true">
              <span
                className="block h-full rounded-full bg-primary"
                style={{ width: `${longest > 0 ? (cost.seconds / longest) * 100 : 0}%` }}
              />
            </span>
            <span className="truncate">{cost.phase}</span>
          </li>
        ))}
      </ul>
    </div>
  )
}

/**
 * How much of the refresh the phases below actually account for. Saying only what they sum to would
 * leave a reader unable to tell 28 s of 30 s from 28 s of 82 s — which is the shape of the no-op
 * refresh that sat 82 seconds on one message and started all of this (#92). What is left over is the
 * queue wait before the first phase and whatever else no phase covers, so it is named as such rather
 * than quietly folded into the last bar.
 */
function Attribution({
  phases,
  total,
  wallSeconds,
}: {
  phases: PhaseCost[]
  total: number
  wallSeconds: number | null
}) {
  if (wallSeconds === null)
    return (
      <>
        {formatSeconds(total)} across {phases.length} phases so far
      </>
    )

  const unattributed = wallSeconds - total
  return (
    <>
      Spent {formatSeconds(total)} of {formatSeconds(wallSeconds)} across {phases.length} phases
      {/* Below a twentieth of a second there is nothing to explain, and naming it would read as a
          problem rather than as the rounding and the queue wait that it is. */}
      {unattributed >= 0.05 ? ` · ${formatSeconds(unattributed)} outside them` : null}
    </>
  )
}

/**
 * A stable key per row, counted rather than taken from the array position. A phase is recorded again
 * whenever the reported text changes back to it, so the same step and text can legitimately appear
 * twice in one timeline and the pair alone would collide.
 */
function keyed(phases: PhaseCost[]): { cost: PhaseCost; key: string }[] {
  const seen = new Map<string, number>()
  return phases.map((cost) => {
    const id = `${cost.step}-${cost.phase}`
    const occurrence = seen.get(id) ?? 0
    seen.set(id, occurrence + 1)
    return { cost, key: `${id}-${occurrence}` }
  })
}

/**
 * Two decimals up to a minute and none beyond it, because a phase is read against the phases beside
 * it and milliseconds stop being the comparison once one runs into minutes — which the full-text
 * rebuild does on a real index. Never "0.00s": the server keeps milliseconds precisely so that a
 * phase which happened is not reported as free, and rounding it away here would undo that.
 */
function formatSeconds(seconds: number): string {
  if (seconds >= 60) return `${Math.round(seconds)}s`
  if (seconds > 0 && seconds < 0.01) return '<0.01s'
  return `${seconds.toFixed(2)}s`
}
